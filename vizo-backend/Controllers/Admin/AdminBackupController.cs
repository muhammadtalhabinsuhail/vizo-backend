using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Backup history. Running a real dump is a pg_dump job, not a web request --
/// POST backups/run records the intent.
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
public class AdminBackupController : AdminControllerBase
{
    public AdminBackupController(AppDbContext db, IConfiguration cfg, ILogger<AdminBackupController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  BACKUP
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("backups")]
    public async Task<IActionResult> GetBackups()
    {
        try
        {
            return Ok(await _db.BackupHistories
                    .OrderByDescending(b => b.StartedAt)
                    .Select(b => new
                    {
                        id = b.BackupId,
                        startedAt = b.StartedAt,
                        type = b.BackupType.TypeName,
                        typeKey = b.BackupType.TypeKey,
                        status = b.Status.StatusName,
                        statusKey = b.Status.StatusKey,
                        sizeMb = b.SizeMb,
                        destination = b.Destination,
                        durationSeconds = b.DurationSeconds,
                        hash = b.ChecksumHash,
                        triggeredBy = b.TriggeredByUser != null ? b.TriggeredByUser.FullName : "Scheduler"
                    })
                    .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/backups");
        }
    }

    [HttpGet("backups/stats")]
    public async Task<IActionResult> BackupStats()
    {
        try
        {
            var last = await _db.BackupHistories.OrderByDescending(b => b.StartedAt)
                .Select(b => new { b.StartedAt, status = b.Status.StatusName }).FirstOrDefaultAsync();

            return Ok(new
            {
                lastBackupAt = last != null ? last.StartedAt : (DateTime?)null,
                lastBackupStatus = last?.status,
                totalSizeMb = await _db.BackupHistories.SumAsync(b => (decimal?)b.SizeMb) ?? 0m,
                retained = await _db.BackupHistories.CountAsync(),
                successRate = await _db.BackupHistories.AnyAsync()
                    ? (int)Math.Round(100.0 * await _db.BackupHistories.CountAsync(b => b.Status.StatusKey == "SUCCESS")
                                      / await _db.BackupHistories.CountAsync())
                    : 0
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/backups/stats");
        }
    }

    /// <summary>
    /// Records a backup run. It writes the history row the screen lists --
    /// taking the actual dump is the job of pg_dump on a schedule, not of a
    /// web request, so this endpoint deliberately does not shell out.
    /// </summary>
    [HttpPost("backups/run")]
    public async Task<IActionResult> RunBackup([FromBody] BackupRequest? body)
    {
        try
        {
            var typeKey = string.IsNullOrWhiteSpace(body?.TypeKey) ? "MANUAL" : body!.TypeKey!.ToUpperInvariant();
            var type = await _db.BackupTypes.FirstOrDefaultAsync(t => t.TypeKey == typeKey)
                       ?? await _db.BackupTypes.FirstAsync();
            var running = await _db.BackupStatuses.FirstAsync(s => s.StatusKey == "RUNNING");

            var row = new BackupHistory
            {
                StartedAt = Now(),
                BackupTypeId = type.BackupTypeId,
                StatusId = running.StatusId,
                SizeMb = 0,
                Destination = string.IsNullOrWhiteSpace(body?.Destination) ? "Manual download" : body!.Destination!,
                DurationSeconds = 0,
                TriggeredByUserId = CurrentUserId()
            };
            _db.BackupHistories.Add(row);
            await _db.SaveChangesAsync();

            await Log("BACKUP_STARTED", "BackupHistory", $"#{row.BackupId}", $"{type.TypeName} backup requested", 1);
            return Ok(new { id = row.BackupId, message = "Backup started. It will appear in the list when it finishes." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/backups/run");
        }
    }

    [HttpGet("backup-types")]
    public async Task<IActionResult> GetBackupTypes()
    {
        try
        {
            return Ok(await _db.BackupTypes.OrderBy(t => t.BackupTypeId)
                    .Select(t => new { id = t.BackupTypeId, key = t.TypeKey, name = t.TypeName }).ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/backup-types");
        }
    }

    // ══════════════════════ request bodies ══════════════════════

    public record BackupRequest(string? TypeKey, string? Destination);
}