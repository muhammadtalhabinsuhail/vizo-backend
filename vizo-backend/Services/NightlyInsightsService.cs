using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The one job that runs on its own, once a night.
///
/// It does two things the map calls for:
///
///   D7  a single "these are running out" notification, bundling every item
///       below its minimum into one message rather than one per product
///   #2  the anomaly check -- today's figures against the trailing 90 days,
///       and only the ones genuinely outside their usual range
///
/// ─────────────────────────── HOW THE ANOMALY CHECK WORKS ───────────────────
///
/// The DEVIATION IS ARITHMETIC, not AI. Mean and standard deviation over 90
/// days, and a figure has to be more than two deviations out before anything is
/// said. AI is only asked to put the survivors into a sentence, and only if
/// something survived.
///
/// That order matters for a reason the brief was explicit about: ask a model
/// "is anything wrong today" every night and it will find something every
/// night, because that is what it was asked for. Ask arithmetic, and on a
/// normal day the answer is nothing and nobody is disturbed.
/// </summary>
public class NightlyInsightsService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NightlyInsightsService> _logger;
    private readonly IConfiguration _cfg;

    public NightlyInsightsService(IServiceProvider services, IConfiguration cfg,
        ILogger<NightlyInsightsService> logger)
    {
        _services = services;
        _cfg = cfg;
        _logger = logger;
    }

    /// <summary>Local time of day to run. Default 20:30.</summary>
    private TimeOnly RunAt =>
        TimeOnly.TryParse(_cfg["NightlyInsights:RunAt"], out var t) ? t : new TimeOnly(20, 30);

    private bool Enabled =>
        !string.Equals(_cfg["NightlyInsights:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Nightly insights are switched off.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = UntilNextRun();
            _logger.LogInformation("Nightly insights will run in {Hours:N1} hours.", delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return; // shutting down
            }

            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var push = scope.ServiceProvider.GetRequiredService<PushNotificationService>();
                var ai = scope.ServiceProvider.GetRequiredService<GeminiClient>();

                await RunLowStockAsync(db, push, stoppingToken);
                await RunAnomalyCheckAsync(db, push, ai, stoppingToken);
            }
            catch (Exception ex)
            {
                /* A failed nightly job must never stop the loop -- otherwise one
                   bad night silently ends the feature until somebody restarts
                   the API. */
                _logger.LogError(ex, "The nightly insights run failed.");
            }
        }
    }

    private TimeSpan UntilNextRun()
    {
        var now = DateTime.Now;
        var next = now.Date.Add(RunAt.ToTimeSpan());
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }

    // ══════════════════════════════════════════════════════════════════
    //  D7 -- what is running out
    // ══════════════════════════════════════════════════════════════════

    private async Task RunLowStockAsync(AppDbContext db, PushNotificationService push, CancellationToken ct)
    {
        var levels = await db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.MinQty > 0)
            .Select(p => new
            {
                p.ProductId,
                p.ProductName,
                p.MinQty,
                OnHand = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
            })
            .ToListAsync(ct);

        var low = levels.Where(p => p.OnHand < p.MinQty).OrderBy(p => p.OnHand).ToList();
        if (low.Count == 0) return;

        /* ONE notification for the lot. Seven separate ones would be seven
           interruptions saying the same thing, and by the third nobody reads
           any of them. The worst offender is named because that is the one
           that decides whether this is urgent. */
        var worst = low[0];
        var body =
            $"{low.Count} {(low.Count == 1 ? "item is" : "items are")} below the minimum. " +
            $"Worst: {worst.ProductName} -- {worst.OnHand} left, minimum {worst.MinQty}.";

        await push.NotifyRolesAsync(
            new[] { "super-admin", "order-dept" },
            NotificationKinds.LowStock,
            "Stock running out",
            body,
            url: "/inventory/stock");

        _logger.LogInformation("Low-stock notice sent for {Count} items.", low.Count);
    }

    // ══════════════════════════════════════════════════════════════════
    //  #2 -- the anomaly check
    // ══════════════════════════════════════════════════════════════════

    private record Series(string Name, string Unit, decimal Today, decimal Mean, decimal StdDev)
    {
        /// <summary>How many standard deviations from normal.</summary>
        public decimal Z => StdDev <= 0 ? 0 : (Today - Mean) / StdDev;
    }

    private async Task RunAnomalyCheckAsync(
        AppDbContext db, PushNotificationService push, GeminiClient ai, CancellationToken ct)
    {
        var today = BusinessClock.Today();
        var since = today.AddDays(-90);

        /* Daily totals for the three things worth watching. */
        var salesByDay = await db.SalesInvoices.AsNoTracking()
            .Where(i => i.Status.StatusKey != "CANCELLED" && i.InvoiceDate >= since && i.InvoiceDate <= today)
            .GroupBy(i => i.InvoiceDate)
            .Select(g => new { Day = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToListAsync(ct);

        var expenseByDay = await db.Expenses.AsNoTracking()
            .Where(e => e.Status.StatusKey == "POSTED" && e.ExpenseDate >= since && e.ExpenseDate <= today)
            .GroupBy(e => e.ExpenseDate)
            .Select(g => new { Day = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var receiptsByDay = await db.Vouchers.AsNoTracking()
            .Where(v => v.Status.StatusKey == "POSTED" && v.VoucherType.IsReceipt
                     && v.VoucherDate >= since && v.VoucherDate <= today)
            .GroupBy(v => v.VoucherDate)
            .Select(g => new { Day = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var series = new List<Series>();
        AddSeries(series, "Sales", salesByDay.ToDictionary(x => x.Day, x => x.Total), today);
        AddSeries(series, "Expenses", expenseByDay.ToDictionary(x => x.Day, x => x.Total), today);
        AddSeries(series, "Money received", receiptsByDay.ToDictionary(x => x.Day, x => x.Total), today);

        /* Two deviations. Below that is an ordinary day, and a notification
           about an ordinary day is how people learn to ignore notifications. */
        var odd = series.Where(s => Math.Abs(s.Z) >= 2m).ToList();
        if (odd.Count == 0)
        {
            _logger.LogInformation("Nightly anomaly check: nothing unusual.");
            return;
        }

        var headline = odd
            .OrderByDescending(s => Math.Abs(s.Z))
            .Select(s => $"{s.Name} {(s.Today < s.Mean ? "down" : "up")} " +
                         $"{Math.Abs(PercentOff(s)):N0}% on normal")
            .ToList();

        var body = string.Join("; ", headline) + ".";

        /* AI, if it is available, and only to say it better. The numbers above
           were already decided; a failure here loses the wording, not the
           alert. */
        var facts = JsonSerializer.Serialize(new
        {
            date = today,
            windowDays = 90,
            unusual = odd.Select(s => new
            {
                s.Name, s.Unit, today = s.Today, normal = Math.Round(s.Mean, 0),
                deviations = Math.Round(s.Z, 1), percentOff = Math.Round(PercentOff(s), 0)
            })
        });

        var written = await ai.ExplainAsync(
            "These figures are outside their normal range for this shop today. " +
            "In at most two sentences say what stands out and what to check tomorrow. " +
            "Do not speculate about causes the data does not show.",
            facts, ct);

        await push.NotifyRoleAsync(
            "super-admin",
            NotificationKinds.Anomaly,
            "Something looks unusual today",
            written ?? body,
            url: "/reports/sales-summary",
            severe: false);

        _logger.LogInformation("Anomaly notice sent: {Body}", body);
    }

    private static decimal PercentOff(Series s) =>
        s.Mean == 0 ? 0 : (s.Today - s.Mean) / s.Mean * 100m;

    /// <summary>
    /// Mean and standard deviation over the window, and today's figure.
    ///
    /// Days with no rows count as ZERO, not as missing. A day the shop sold
    /// nothing is exactly the sort of day this is meant to notice, and dropping
    /// it from the average would hide it.
    /// </summary>
    private static void AddSeries(
        List<Series> into, string name, Dictionary<DateOnly, decimal> byDay, DateOnly today)
    {
        var window = Enumerable.Range(1, 90)
            .Select(d => today.AddDays(-d))
            .Select(d => byDay.TryGetValue(d, out var v) ? v : 0m)
            .ToList();

        /* Not enough history to know what normal looks like. Saying something is
           unusual after four days of data is guessing with extra steps. */
        if (window.Count(v => v > 0) < 10) return;

        var mean = window.Average();
        var variance = window.Sum(v => (v - mean) * (v - mean)) / window.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        var todayValue = byDay.TryGetValue(today, out var t) ? t : 0m;
        into.Add(new Series(name, "PKR", todayValue, mean, stdDev));
    }
}
