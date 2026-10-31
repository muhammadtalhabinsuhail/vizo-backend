using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The nudge that keeps collected money from staying in a rep's pocket.
///
/// A salesperson records a payment collection (SalesCollectionsController) and
/// it sits AWAITING until the accountant or the Super Admin confirms it. Until
/// then the rep is reminded -- every few hours, six by default -- to hand the
/// money in (the owner, 1 Nov: "salesperson will remain get notifications to
/// submit payment to accountant or superadmin").
///
/// As with ConfirmReminderService the last reminder is a column
/// ("Collection"."RemindedAt"), not a timer, so a restart neither forgets the
/// schedule nor fires a burst of duplicates. The loop wakes every fifteen
/// minutes and sends to whoever is due -- one message per rep, covering all of
/// their waiting collections.
/// </summary>
public class CollectionReminderService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CollectionReminderService> _logger;

    public CollectionReminderService(IServiceProvider services, IConfiguration cfg,
        ILogger<CollectionReminderService> logger)
    {
        _services = services;
        _cfg = cfg;
        _logger = logger;
    }

    /// <summary>Hours between nudges. Six unless configured otherwise.</summary>
    private int EveryHours =>
        int.TryParse(_cfg["CollectionReminder:EveryHours"], out var h) && h > 0 ? h : 6;

    private bool Enabled =>
        !string.Equals(_cfg["CollectionReminder:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _logger.LogInformation("Collection reminders are switched off.");
            return;
        }

        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                /* Swallowed on purpose: a background service that dies takes
                   every future reminder with it. */
                _logger.LogError(ex, "The collection reminder sweep failed.");
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

        var due = await db.Collections
            .Where(c => c.Status.StatusKey == "AWAITING")
            .Where(c => c.RemindedAt == null || c.RemindedAt <= cutoff)
            .OrderBy(c => c.CollectedOn)
            .Take(200)
            .ToListAsync(ct);
        if (due.Count == 0) return;

        var names = await db.Parties.AsNoTracking()
            .Where(p => due.Select(c => c.CustomerUserId).Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, p => p.DisplayName ?? p.LegalName, ct);

        foreach (var rep in due.GroupBy(c => c.CollectedByUserId))
        {
            var list = rep.ToList();
            var total = list.Sum(c => c.Amount);
            var lead = list.Take(3)
                .Select(c => $"{c.ReceiptNo} (PKR {c.Amount:N0}, {names.GetValueOrDefault(c.CustomerUserId, "a customer")})")
                .ToList();
            var rest = list.Count - lead.Count;
            var oldestDays = now.Date.Subtract(list.Min(c => c.CollectedOn).ToDateTime(TimeOnly.MinValue)).Days;

            var body = list.Count == 1
                ? $"You still hold PKR {total:N0} collected as {lead[0]}. Hand it to the accountant or the Super Admin so it can be confirmed."
                : $"You still hold PKR {total:N0} from {list.Count} collections: {string.Join(", ", lead)}" +
                  (rest > 0 ? $" and {rest} more" : "") +
                  ". Hand it to the accountant or the Super Admin so it can be confirmed.";

            await push.NotifyAsync(rep.Key, NotificationKinds.CollectionReminder,
                "Hand in your collected payment", body, url: "/sales/collections", severe: oldestDays >= 2);

            foreach (var c in list) c.RemindedAt = now;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Reminded reps about {Count} collection(s) awaiting confirmation.", due.Count);
    }
}
