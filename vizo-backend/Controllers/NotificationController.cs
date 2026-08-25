using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// The bell in the top bar. Open to any signed-in member of staff, not just
/// the Super Admin -- every portal shows it, and each person only ever sees
/// their own rows.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "Staff")]
public class NotificationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<NotificationController> _logger;
    private readonly IWebHostEnvironment _env;

    public NotificationController(AppDbContext db,
        ILogger<NotificationController> logger, IWebHostEnvironment env)
    {
        _db = db;
        _logger = logger;
        _env = env;
    }

    private int CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int take = 20)
    {
        try
        {
            var me = CurrentUserId();

            var items = await _db.Notifications
                .Where(n => n.UserId == me)
                .OrderByDescending(n => n.CreatedAt)
                .Take(take)
                .Select(n => new
                {
                    id = n.NotificationId,
                    severity = n.Severity.SeverityKey,
                    icon = n.Icon,
                    title = n.Title,
                    body = n.Body,
                    createdAt = n.CreatedAt,
                    isRead = n.IsRead
                })
                .ToListAsync();

            return Ok(new
            {
                items,
                unread = await _db.Notifications.CountAsync(n => n.UserId == me && !n.IsRead)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load C:/Program Files/Git/api/notification");
        }
    }

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        try
        {
            var me = CurrentUserId();
            /* Scoped to the caller: an id from somebody else's list must not be
               reachable just by guessing the number. */
            var row = await _db.Notifications.FirstOrDefaultAsync(n => n.NotificationId == id && n.UserId == me);
            if (row is null) return NotFound(new { message = "Notification not found." });

            row.IsRead = true;
            await _db.SaveChangesAsync();
            return Ok(new { message = "Marked as read." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/notification/{id:int}/read");
        }
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        try
        {
            var me = CurrentUserId();
            var rows = await _db.Notifications.Where(n => n.UserId == me && !n.IsRead).ToListAsync();
            foreach (var r in rows) r.IsRead = true;
            await _db.SaveChangesAsync();
            return Ok(new { message = $"{rows.Count} marked as read." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/notification/read-all");
        }
    }

    /// <summary>
    /// The single failure path for this controller.
    ///
    /// Logs the whole exception server-side, then answers with JSON the screen
    /// can show: what was being attempted, and the real message off the BASE
    /// exception -- Npgsql puts the useful text there (a constraint name, a
    /// null violation) while the outer DbUpdateException only ever says
    /// "An error occurred while saving the entity changes".
    ///
    /// The stack trace is attached in Development only.
    /// </summary>
    private IActionResult Fail(Exception ex, string what)
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

}
