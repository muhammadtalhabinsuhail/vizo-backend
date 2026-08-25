using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// The activity trail. Every mutation the API accepts writes a row here, which
/// makes this the fastest way to tell a real save from a fake one.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match exactly what the screen
/// renders.
///
/// Every action is wrapped in try/catch and reports through Fail(), so a failure
/// reaches the browser as JSON with the real exception message instead of an
/// empty 500. See AdminControllerBase.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = "SuperAdmin")]
public class AdminAuditLogController : AdminControllerBase
{
    public AdminAuditLogController(AppDbContext db, IConfiguration cfg, ILogger<AdminAuditLogController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  AUDIT LOG
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] string? q, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] string? severity, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        try
        {
            var query = _db.ActivityLogs.AsQueryable();

            if (from.HasValue)
                query = query.Where(a => a.LoggedAt >= from.Value.ToDateTime(TimeOnly.MinValue));
            if (to.HasValue)
                query = query.Where(a => a.LoggedAt <= to.Value.ToDateTime(TimeOnly.MaxValue));
            if (!string.IsNullOrWhiteSpace(severity) && severity != "all")
                query = query.Where(a => a.Severity.SeverityKey == severity);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(a =>
                    (a.User != null && a.User.FullName.ToLower().Contains(term)) ||
                    a.ActionName.ToLower().Contains(term) ||
                    a.EntityType.ToLower().Contains(term) ||
                    a.EntityReference.ToLower().Contains(term));
            }

            var total = await query.CountAsync();

            var items = await query
                .OrderByDescending(a => a.LoggedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(a => new
                {
                    id = a.LogId,
                    user = a.User != null ? a.User.FullName : "System",
                    action = a.ActionName,
                    entityType = a.EntityType,
                    entityReference = a.EntityReference,
                    entity = a.EntityType + " " + a.EntityReference,
                    detail = a.Detail,
                    time = a.LoggedAt,
                    ip = a.IpAddress ?? "internal",
                    location = a.Location != null ? a.Location.LocationName : null,
                    severity = a.Severity.SeverityKey
                })
                .ToListAsync();

            return Ok(new { items, total, page, pageSize });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/audit-log");
        }
    }

    [HttpGet("audit-log/stats")]
    public async Task<IActionResult> AuditStats()
    {
        try
        {
            var todayStart = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Unspecified);
            var since = todayStart.AddDays(-1);

            return Ok(new
            {
                totalToday = await _db.ActivityLogs.CountAsync(a => a.LoggedAt >= todayStart),
                failedLogins = await _db.ActivityLogs.CountAsync(a => a.ActionName == "LOGIN_FAIL" && a.LoggedAt >= since),
                permissionChanges = await _db.ActivityLogs.CountAsync(a =>
                    (a.EntityType == "Role" || a.EntityType == "User") && a.ActionName == "UPDATED" && a.LoggedAt >= since),
                recentLogins = await _db.ActivityLogs.CountAsync(a => a.ActionName == "LOGIN" && a.LoggedAt >= since)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/audit-log/stats");
        }
    }

    [HttpGet("audit-log/{id:int}")]
    public async Task<IActionResult> GetAuditEntry(int id)
    {
        try
        {
            var a = await _db.ActivityLogs
                .Where(x => x.LogId == id)
                .Select(x => new
                {
                    id = x.LogId,
                    user = x.User != null ? x.User.FullName : "System",
                    userEmail = x.User != null ? x.User.Email : null,
                    action = x.ActionName,
                    entityType = x.EntityType,
                    entityReference = x.EntityReference,
                    entity = x.EntityType + " " + x.EntityReference,
                    detail = x.Detail,
                    time = x.LoggedAt,
                    ip = x.IpAddress ?? "internal",
                    location = x.Location != null ? x.Location.LocationName : null,
                    severity = x.Severity.SeverityKey
                })
                .FirstOrDefaultAsync();

            if (a is null) return NotFound(new { message = "Entry not found." });
            return Ok(a);
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/audit-log/{id:int}");
        }
    }

    [HttpGet("severity-levels")]
    public async Task<IActionResult> GetSeverities()
    {
        try
        {
            return Ok(await _db.SeverityLevels.OrderBy(s => s.SeverityId)
                    .Select(s => new { id = s.SeverityId, key = s.SeverityKey, name = s.SeverityName }).ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/severity-levels");
        }
    }

}