using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Data;

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
    public NotificationController(AppDbContext db) => _db = db;

    private int CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int take = 20)
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

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
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

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var me = CurrentUserId();
        var rows = await _db.Notifications.Where(n => n.UserId == me && !n.IsRead).ToListAsync();
        foreach (var r in rows) r.IsRead = true;
        await _db.SaveChangesAsync();
        return Ok(new { message = $"{rows.Count} marked as read." });
    }
}
