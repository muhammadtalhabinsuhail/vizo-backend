using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Shared plumbing for EVERY controller in this API -- admin and non-admin alike.
///
/// This is NOT a service layer. It is plain ASP.NET controller inheritance:
/// no interface, nothing registered in DI, nothing injected anywhere. It exists
/// so the same five helpers are not copy-pasted into ten files, and so every
/// action reports failure the same way.
///
/// The rule the whole API follows: EVERY action body sits inside try/catch,
/// and the catch calls <see cref="Fail"/>. Before this, 46 admin actions had
/// zero try/catch between them -- any exception surfaced to the browser as a
/// bare 500 with an empty body, which is why failures looked like "nothing
/// happened" instead of "this is what went wrong".
/// </summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected readonly AppDbContext _db;
    protected readonly IConfiguration _cfg;
    protected readonly ILogger _logger;
    protected readonly IWebHostEnvironment _env;

    protected ApiControllerBase(AppDbContext db, IConfiguration cfg,
        ILogger logger, IWebHostEnvironment env)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
        _env = env;
    }

    /// <summary>
    /// The single failure path for every admin action.
    ///
    /// Logs the whole exception server-side, then answers with JSON the screen
    /// can actually show: what was being attempted, and the real message off
    /// the base exception (Npgsql puts the useful text there -- a constraint
    /// name, a null violation -- while the outer DbUpdateException only ever
    /// says "An error occurred while saving the entity changes").
    ///
    /// The stack trace is attached in Development only. It is the thing you
    /// want while building and the thing you must not ship.
    /// </summary>
    protected IActionResult Fail(Exception ex, string what)
    {
        _logger.LogError(ex, "Failed to {What} ({Method} {Path})",
            what, Request.Method, Request.Path);

        return StatusCode(500, new
        {
            message = $"Could not {what}.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = _env.IsDevelopment() ? ex.ToString() : null
        });
    }

    /* "timestamp without time zone" columns reject a Utc-kind DateTime, so every
       timestamp written goes through here. */
    protected static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    protected static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    protected int CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    /* NOTE for anyone reading the scaffolded models: "User" carries TWO
       location collections and they are easy to mix up.
           User.Locations           -> locations this person is IN CHARGE OF
                                       (inverse of Location.InChargeUserId)
           User.LocationsNavigation -> the UserLocation junction, i.e. the
                                       locations they may WORK OUT OF
       Access control wants the second one. */

    protected static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    protected async Task Log(string action, string entityType, string reference,
        string? detail, int severityId)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            UserId = CurrentUserId(),
            ActionName = action,
            EntityType = entityType,
            EntityReference = reference,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            SeverityId = severityId,
            LoggedAt = Now()
        });
        await _db.SaveChangesAsync();
    }
}
