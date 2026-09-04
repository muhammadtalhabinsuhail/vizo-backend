using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Where a browser registers itself for notifications, and where a person
/// chooses which ones they want.
///
/// A subscription belongs to a BROWSER, not to a person: the same user on a
/// laptop, a phone and the shop terminal produces three rows, and a push has to
/// reach all three.
/// </summary>
[Route("api/push")]
[ApiController]
[Authorize(Policy = "Staff")]
public class PushController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public PushController(AppDbContext db, IConfiguration cfg,
        ILogger<PushController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /// <summary>
    /// What the settings screen needs before it offers the switch: whether the
    /// server can push at all, and the public key the browser must subscribe
    /// with.
    ///
    /// The public key is public by design -- it identifies this application to
    /// the push service. The private key never leaves the server.
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        try
        {
            return Ok(new
            {
                enabled = _push.PushConfigured,
                publicKey = _cfg["VapidSettings:PublicKey"] ?? ""
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "read the push configuration");
        }
    }

    /// <summary>
    /// Register this browser. Idempotent: a browser that re-subscribes after a
    /// service-worker update sends the same endpoint, and that must update the
    /// existing row rather than add a second one -- otherwise the person starts
    /// receiving everything twice.
    /// </summary>
    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Endpoint) ||
                string.IsNullOrWhiteSpace(body.P256dh) ||
                string.IsNullOrWhiteSpace(body.Auth))
                return BadRequest(new { message = "A push subscription needs an endpoint and both keys." });

            var me = CurrentUserId();
            var agent = Request.Headers.UserAgent.ToString();
            if (agent.Length > 300) agent = agent[..300];

            var existing = await _db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == body.Endpoint);

            if (existing is not null)
            {
                /* The same browser, possibly a different person -- a shared
                   shop terminal where somebody signed out and somebody else
                   signed in. The subscription now belongs to whoever is
                   actually using it. */
                existing.UserId = me;
                existing.P256dh = body.P256dh;
                existing.Auth = body.Auth;
                existing.UserAgent = agent;
                await _db.SaveChangesAsync();

                return Ok(new { message = "This browser is already set up for notifications." });
            }

            _db.PushSubscriptions.Add(new Models.PushSubscription
            {
                UserId = me,
                Endpoint = body.Endpoint,
                P256dh = body.P256dh,
                Auth = body.Auth,
                UserAgent = agent,
                CreatedAt = Now()
            });
            await _db.SaveChangesAsync();

            return Ok(new { message = "Notifications are on for this browser." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "register this browser for notifications");
        }
    }

    /// <summary>Stop notifications on this browser.</summary>
    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] UnsubscribeRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Endpoint))
                return BadRequest(new { message = "Which browser? An endpoint is required." });

            var row = await _db.PushSubscriptions
                .FirstOrDefaultAsync(s => s.Endpoint == body.Endpoint && s.UserId == CurrentUserId());

            if (row is not null)
            {
                _db.PushSubscriptions.Remove(row);
                await _db.SaveChangesAsync();
            }

            /* Not found is still success. The caller wanted this browser to
               stop receiving notifications, and it is not receiving any. */
            return Ok(new { message = "Notifications are off for this browser." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "turn notifications off for this browser");
        }
    }

    /// <summary>The browsers this person currently has switched on.</summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        try
        {
            var me = CurrentUserId();
            var rows = await _db.PushSubscriptions.AsNoTracking()
                .Where(s => s.UserId == me)
                .OrderByDescending(s => s.CreatedAt)
                .Select(s => new
                {
                    id = s.PushSubscriptionId,
                    /* Never return the endpoint. It is the address that can
                       send this person a notification, and it has no business
                       in a response that only needs to label a row. */
                    device = s.UserAgent,
                    since = s.CreatedAt,
                    lastUsed = s.LastUsedAt
                })
                .ToListAsync();

            return Ok(new { count = rows.Count, items = rows });
        }
        catch (Exception ex)
        {
            return Fail(ex, "list the browsers set up for notifications");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  WHICH NOTIFICATIONS THIS PERSON WANTS
    // ══════════════════════════════════════════════════════════════════

    /*  Shipped with the first notification, not after it.

        Without a way to switch individual kinds off, people mute the whole
        thing within a fortnight -- and then the credit-limit alert, the one
        that actually matters, stops arriving too.

        A MISSING ROW MEANS ON, so a new kind starts switched on for everybody
        with no backfill and the table only ever holds deliberate exceptions. */

    /// <summary>
    /// Every notification kind, with what this person has chosen for it.
    /// </summary>
    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        try
        {
            var me = CurrentUserId();
            var saved = await _db.NotificationPreferences.AsNoTracking()
                .Where(p => p.UserId == me)
                .ToDictionaryAsync(p => p.Kind);

            var items = NotificationKinds.All
                .Select(k => new
                {
                    kind = k.Key,
                    group = k.Group,
                    label = k.Label,
                    description = k.Description,
                    pushEnabled = saved.TryGetValue(k.Key, out var s1) ? s1.PushEnabled : true,
                    bellEnabled = saved.TryGetValue(k.Key, out var s2) ? s2.BellEnabled : true
                })
                .ToList();

            return Ok(new
            {
                pushConfigured = _push.PushConfigured,
                groups = items.GroupBy(i => i.group).Select(g => new { group = g.Key, items = g.ToList() })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the notification preferences");
        }
    }

    /// <summary>Turn one kind on or off for the signed-in person.</summary>
    [HttpPut("preferences")]
    public async Task<IActionResult> SetPreference([FromBody] PreferenceRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Kind))
                return BadRequest(new { message = "Which notification? A kind is required." });

            if (!NotificationKinds.All.Any(k => k.Key == body.Kind))
                return BadRequest(new { message = $"'{body.Kind}' is not a notification this app sends." });

            var me = CurrentUserId();
            var row = await _db.NotificationPreferences
                .FirstOrDefaultAsync(p => p.UserId == me && p.Kind == body.Kind);

            if (body.PushEnabled && body.BellEnabled)
            {
                /* Back to the default. Delete the row rather than store it --
                   the table is for exceptions only. */
                if (row is not null)
                {
                    _db.NotificationPreferences.Remove(row);
                    await _db.SaveChangesAsync();
                }
                return Ok(new { message = "Back to the default for this notification." });
            }

            if (row is null)
            {
                _db.NotificationPreferences.Add(new NotificationPreference
                {
                    UserId = me,
                    Kind = body.Kind,
                    PushEnabled = body.PushEnabled,
                    BellEnabled = body.BellEnabled
                });
            }
            else
            {
                row.PushEnabled = body.PushEnabled;
                row.BellEnabled = body.BellEnabled;
            }

            await _db.SaveChangesAsync();
            return Ok(new { message = "Saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the notification preference");
        }
    }

    /// <summary>
    /// Send this person a test notification, so they can see whether it
    /// actually arrives before they trust it with anything real.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTest()
    {
        try
        {
            var me = CurrentUserId();

            if (!await _db.PushSubscriptions.AnyAsync(s => s.UserId == me))
                return BadRequest(new { message = "This browser is not set up for notifications yet." });

            await _push.NotifyAsync(
                me,
                NotificationKinds.Test,
                "Test notification",
                "If you can read this, notifications are working on this device.",
                url: "/profile/notifications");

            return Ok(new { message = "Sent. It should appear in a moment." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "send the test notification");
        }
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record SubscribeRequest(string Endpoint, string P256dh, string Auth);
    public record UnsubscribeRequest(string Endpoint);
    public record PreferenceRequest(string Kind, bool PushEnabled, bool BellEnabled);
}
