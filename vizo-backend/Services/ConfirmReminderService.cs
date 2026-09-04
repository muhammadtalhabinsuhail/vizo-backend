using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The nudge that keeps a submitted order from going quiet.
///
/// ─────────────────────────── WHAT IT DOES ──────────────────────────────────
///
/// An order sits at SUBMITTED until the Super Admin either confirms it or
/// declines it. Nothing downstream can happen while it sits there -- the
/// warehouse cannot pick it, the order desk cannot pack it, and the customer is
/// waiting on a decision nobody has been reminded to make.
///
/// So every six hours, for as long as an order is still waiting, this reminds
/// the Super Admin. The brief asked for exactly that, in those words: a
/// reminder every six hours until it is CONFIRMED or DECLINED.
///
/// ─────────────────────────── WHY IT IS A COLUMN AND NOT A TIMER ────────────
///
/// The last reminder is written to "SalesOrder"."ConfirmRemindedAt". That is
/// deliberate. An in-memory timer forgets everything the moment the API
/// restarts -- which, on a machine that redeploys, means either a fresh six
/// hours of silence or a burst of duplicate reminders on every boot. A column
/// survives the restart, so the schedule is the order's, not the process's.
///
/// The loop wakes every fifteen minutes and sends to whatever is due. It does
/// not wake every six hours, because "due" is per order: one submitted at 09:00
/// and one at 11:00 are on different clocks.
/// </summary>
public class ConfirmReminderService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ConfirmReminderService> _logger;

    public ConfirmReminderService(IServiceProvider services, IConfiguration cfg,
        ILogger<ConfirmReminderService> logger)
    {
        _services = services;
        _cfg = cfg;
        _logger = logger;
    }

    /// <summary>Hours between nudges. Six unless configured otherwise.</summary>
    private int EveryHours =>
        int.TryParse(_cfg["ConfirmReminder:EveryHours"], out var h) && h > 0 ? h : 6;

    private bool Enabled =>
        !string.Equals(_cfg["ConfirmReminder:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Order confirmation reminders are switched off.");
            return;
        }

        /* A short wait on boot so the reminder sweep is not competing with
           migrations and the first requests for the connection pool. */
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                /* Swallowed on purpose. A reminder that cannot be sent is a
                   nuisance; a background service that dies takes every future
                   reminder with it. */
                _logger.LogError(ex, "The confirmation reminder sweep failed.");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var push = scope.ServiceProvider.GetRequiredService<PushNotificationService>();

        var now = BusinessClock.Now();
        var cutoff = now.AddHours(-EveryHours);

        /* Due means: still submitted, and last spoken about more than one
           interval ago.

           The clock is started at submission, where ConfirmRemindedAt is
           stamped alongside the "order submitted" notification -- that message
           IS the first reminder, so the next one falls six hours later rather
           than fifteen minutes later. A null here is an order that predates the
           column, and those are chased on the next tick. */
        var due = await db.SalesOrders
            .Where(o => o.Status.StatusKey == OrderWorkflow.Submitted)
            .Where(o => o.ConfirmRemindedAt == null || o.ConfirmRemindedAt <= cutoff)
            .OrderBy(o => o.OrderDate)
            .Take(50)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        /* One message covering all of them, not one per order. Twelve separate
           buzzes at six in the morning is how a person learns to ignore the
           bell. */
        var names = await db.Parties.AsNoTracking()
            .Where(p => due.Select(o => o.CustomerUserId).Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, p => p.LegalName, ct);

        var total = due.Sum(o => o.TotalAmount);
        var oldest = due.Min(o => o.ConfirmRemindedAt ?? o.CreatedAt.ToDateTime(TimeOnly.MinValue));
        var waiting = (int)Math.Round((now - oldest).TotalHours);

        var lead = due.Take(3)
            .Select(o => $"{o.OrderNo} ({names.GetValueOrDefault(o.CustomerUserId, "a customer")})")
            .ToList();
        var rest = due.Count - lead.Count;

        var body = due.Count == 1
            ? $"{lead[0]} is still waiting for your decision -- {waiting}h now. PKR {total:N0}."
            : $"{due.Count} orders are waiting for you to confirm or decline them -- " +
              $"PKR {total:N0} in total. {string.Join(", ", lead)}" +
              (rest > 0 ? $" and {rest} more." : ".");

        await push.NotifyRolesAsync(
            new[] { OrderWorkflow.RoleAdmin },
            NotificationKinds.OrderCreated,
            due.Count == 1 ? "Order still waiting to be confirmed" : "Orders waiting to be confirmed",
            body,
            url: "/sales/orders?status=SUBMITTED",
            severe: waiting >= 24);

        foreach (var o in due) o.ConfirmRemindedAt = now;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Reminded the owner about {Count} unconfirmed order(s).", due.Count);
    }
}
