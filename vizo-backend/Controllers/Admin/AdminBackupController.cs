using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Backups: take one, list them, download one.
///
/// Until 27 Sep this recorded the INTENT of a backup and nothing else: "Run
/// Backup Now" inserted a RUNNING row that never finished, Download was
/// permanently disabled, and the rows on screen were seed data. Now a run takes
/// a real logical backup of every table (Services/DatabaseBackup -- COPY to
/// CSV, zipped with a manifest), stores the file in "BackupFile", and marks the
/// row SUCCESS with its real size and duration, or FAILED with the reason.
///
/// There is NO restore endpoint, on purpose. Loading a backup replaces every
/// row in the database; that is done by the owner from the file, into a fresh
/// database, following the RESTORE.txt inside it -- not by a button.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match exactly what the screen
/// renders. Every action is wrapped in try/catch and reports through Fail().
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = "SuperAdmin")]
public class AdminBackupController : AdminControllerBase
{
    private readonly PushNotificationService _push;

    public AdminBackupController(AppDbContext db, IConfiguration cfg, ILogger<AdminBackupController> logger,
        IWebHostEnvironment env, PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /// <summary>How many backup FILES are kept. Older runs keep their history row, not their bytes.</summary>
    private int KeepFiles => Math.Max(1, _cfg.GetValue("Backup:KeepFiles", 7));

    /// <summary>A RUNNING row older than this belongs to a run the API never finished (it was restarted).</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);


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
                    .ThenByDescending(b => b.BackupId)
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
                        tableCount = b.TableCount,
                        rowTotal = b.RowTotal,
                        error = b.ErrorMessage,
                        /* The bytes themselves are never read here -- only
                           whether they are still kept. */
                        hasFile = _db.BackupFiles.Any(f => f.BackupId == b.BackupId),
                        sizeBytes = _db.BackupFiles.Where(f => f.BackupId == b.BackupId)
                                                   .Select(f => (long?)f.SizeBytes).FirstOrDefault(),
                        /* Named here as well as in Content-Disposition: CORS
                           does not expose that header to the browser. */
                        fileName = _db.BackupFiles.Where(f => f.BackupId == b.BackupId)
                                                  .Select(f => f.FileName).FirstOrDefault(),
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
            var last = await _db.BackupHistories
                .OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.BackupId)
                .Select(b => new { b.StartedAt, status = b.Status.StatusName, key = b.Status.StatusKey })
                .FirstOrDefaultAsync();
            var lastGood = await _db.BackupHistories
                .Where(b => b.Status.StatusKey == "SUCCESS")
                .OrderByDescending(b => b.StartedAt)
                .Select(b => (DateTime?)b.StartedAt)
                .FirstOrDefaultAsync();

            /* Finished runs only: one still RUNNING is neither a success nor a failure yet. */
            var finished = await _db.BackupHistories.CountAsync(b => b.Status.StatusKey != "RUNNING");
            var succeeded = await _db.BackupHistories.CountAsync(b => b.Status.StatusKey == "SUCCESS");
            var storedBytes = await _db.BackupFiles.SumAsync(f => (long?)f.SizeBytes) ?? 0L;

            return Ok(new
            {
                lastBackupAt = last?.StartedAt,
                lastBackupStatus = last?.status,
                lastBackupStatusKey = last?.key,
                lastSuccessAt = lastGood,
                /* What the stored files take up now -- not the sum of every
                   run ever, most of whose files have been let go. */
                totalSizeMb = Math.Round(storedBytes / 1048576m, 2),
                retained = await _db.BackupFiles.CountAsync(),
                keepFiles = KeepFiles,
                runs = await _db.BackupHistories.CountAsync(),
                successRate = finished > 0 ? (int)Math.Round(100.0 * succeeded / finished) : (int?)null
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/backups/stats");
        }
    }

    /// <summary>
    /// Takes a backup now, in this request, and answers when it is done. The
    /// database is a few MB, so this is seconds; a background job would only
    /// add a row that says RUNNING and a screen that has to poll it.
    /// </summary>
    [HttpPost("backups/run")]
    public async Task<IActionResult> RunBackup([FromBody] BackupRequest? body)
    {
        BackupHistory? row = null;
        var clock = Stopwatch.StartNew();
        try
        {
            var running = await _db.BackupStatuses.FirstAsync(s => s.StatusKey == "RUNNING");
            var success = await _db.BackupStatuses.FirstAsync(s => s.StatusKey == "SUCCESS");
            var failed = await _db.BackupStatuses.FirstAsync(s => s.StatusKey == "FAILED");

            /* A RUNNING row the API never came back to (it was restarted mid-run)
               is a failure, not a run in progress -- say so, or it spins forever. */
            var staleBefore = Now() - Stale;
            var orphans = await _db.BackupHistories
                .Where(b => b.StatusId == running.StatusId && b.StartedAt < staleBefore).ToListAsync();
            foreach (var o in orphans)
            {
                o.StatusId = failed.StatusId;
                o.TableCount ??= 0;
                o.ErrorMessage = "Interrupted: the server stopped before this backup finished.";
            }
            if (orphans.Count > 0) await _db.SaveChangesAsync();

            if (await _db.BackupHistories.AnyAsync(b => b.StatusId == running.StatusId))
                return Conflict(new { message = "A backup is already running. Wait for it to finish." });

            var typeKey = string.IsNullOrWhiteSpace(body?.TypeKey) ? "MANUAL" : body!.TypeKey!.ToUpperInvariant();
            var type = await _db.BackupTypes.FirstOrDefaultAsync(t => t.TypeKey == typeKey)
                       ?? await _db.BackupTypes.FirstAsync(t => t.TypeKey == "MANUAL");

            row = new BackupHistory
            {
                StartedAt = Now(),
                BackupTypeId = type.BackupTypeId,
                StatusId = running.StatusId,
                SizeMb = 0,
                Destination = "Server (download to keep)",
                DurationSeconds = 0,
                /* Never null on a row this code wrote -- migration 38 uses a
                   null here to recognise the old seed rows. */
                TableCount = 0,
                TriggeredByUserId = CurrentUserId()
            };
            _db.BackupHistories.Add(row);
            await _db.SaveChangesAsync();

            DatabaseBackup.Result result;
            try
            {
                result = await DatabaseBackup.CreateAsync(
                    _cfg.GetConnectionString("DefaultConnection")!, row.StartedAt, CurrentUserName(false));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup #{Id} failed", row.BackupId);
                var reason = ex.GetBaseException().Message;
                row.StatusId = failed.StatusId;
                row.DurationSeconds = Seconds(clock);
                row.ErrorMessage = reason.Length > 500 ? reason[..500] : reason;
                await _db.SaveChangesAsync();

                await Log("BACKUP_FAILED", "BackupHistory", $"#{row.BackupId}", row.ErrorMessage, 4);
                await _push.NotifyRoleAsync("super-admin", NotificationKinds.BackupDone,
                    "Backup failed", $"The backup started by {CurrentUserName()} failed: {row.ErrorMessage}",
                    url: "/admin/backup", severe: true);

                return StatusCode(500, new
                {
                    id = row.BackupId,
                    message = "The backup failed. Nothing was stored.",
                    error = row.ErrorMessage
                });
            }

            var fileName = $"advpos-backup-{row.StartedAt:yyyyMMdd-HHmmss}.zip";
            _db.BackupFiles.Add(new BackupFile
            {
                BackupId = row.BackupId,
                FileName = fileName,
                Content = result.Zip,
                SizeBytes = result.Zip.LongLength,
                CreatedAt = Now()
            });

            row.StatusId = success.StatusId;
            row.SizeMb = Math.Round(result.Zip.LongLength / 1048576m, 2);
            row.DurationSeconds = Seconds(clock);
            row.ChecksumHash = "sha256:" + result.Sha256;
            row.TableCount = result.TableCount;
            row.RowTotal = result.RowTotal;
            await _db.SaveChangesAsync();

            await PruneOldFiles();

            await Log("BACKUP_TAKEN", "BackupHistory", $"#{row.BackupId}",
                      $"{result.TableCount} tables, {result.RowTotal:N0} rows, {result.Zip.LongLength:N0} bytes", 2);

            /* -- F3 -- "backup finished" is finally true when it is said. */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.BackupDone,
                $"Backup taken by {CurrentUserName()}",
                $"{result.TableCount} tables, {result.RowTotal:N0} rows -- ready to download.",
                url: "/admin/backup",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = row.BackupId,
                message = $"Backup taken: {result.TableCount} tables, {result.RowTotal:N0} rows. Download it to keep a copy off the server.",
                fileName,
                sizeBytes = result.Zip.LongLength,
                tableCount = result.TableCount,
                rowTotal = result.RowTotal,
                migrationLevel = result.MigrationLevel,
                durationSeconds = row.DurationSeconds
            });
        }
        catch (Exception ex)
        {
            /* Something outside the dump itself went wrong (saving the file,
               say). Do not leave the row RUNNING. */
            if (row is not null && row.BackupId > 0)
            {
                try
                {
                    _db.ChangeTracker.Clear();
                    var stuck = await _db.BackupHistories.FirstOrDefaultAsync(b => b.BackupId == row.BackupId);
                    var failedId = await _db.BackupStatuses.Where(s => s.StatusKey == "FAILED").Select(s => s.StatusId).FirstAsync();
                    if (stuck is not null)
                    {
                        var reason = ex.GetBaseException().Message;
                        stuck.StatusId = failedId;
                        stuck.DurationSeconds = Seconds(clock);
                        stuck.ErrorMessage = reason.Length > 500 ? reason[..500] : reason;
                        await _db.SaveChangesAsync();
                    }
                }
                catch (Exception inner)
                {
                    _logger.LogError(inner, "Could not mark backup #{Id} as failed", row.BackupId);
                }
            }
            return Fail(ex, "run a backup");
        }
    }

    /// <summary>
    /// The zip, as a file. Fetched by the screen with the sign-in header (a
    /// plain link would carry no bearer token -- HANDOFF trap 14).
    /// </summary>
    [HttpGet("backups/{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        try
        {
            var file = await _db.BackupFiles.AsNoTracking().FirstOrDefaultAsync(f => f.BackupId == id);
            if (file is null)
                return NotFound(new { message = "That backup's file is no longer kept -- only the newest few are. Take a new one." });

            await Log("BACKUP_DOWNLOADED", "BackupHistory", $"#{id}", file.FileName, 3);
            return File(file.Content, "application/zip", file.FileName);
        }
        catch (Exception ex)
        {
            return Fail(ex, "download /api/admin/backups/{id:int}");
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

    // ══════════════════════ helpers ══════════════════════

    private static int Seconds(Stopwatch clock) => (int)Math.Ceiling(clock.Elapsed.TotalSeconds);

    /// <summary>
    /// Keeps the newest <see cref="KeepFiles"/> files and lets the bytes of
    /// older ones go; their history row stays, saying so.
    /// </summary>
    private async Task PruneOldFiles()
    {
        var keep = KeepFiles;
        var old = await _db.BackupFiles
            .OrderByDescending(f => f.BackupId)
            .Skip(keep)
            .Select(f => f.BackupId)
            .ToListAsync();
        if (old.Count == 0) return;

        await _db.BackupFiles.Where(f => old.Contains(f.BackupId)).ExecuteDeleteAsync();
        var rows = await _db.BackupHistories.Where(b => old.Contains(b.BackupId)).ToListAsync();
        foreach (var r in rows) r.Destination = $"File let go (the newest {keep} are kept)";
        await _db.SaveChangesAsync();
    }

    // ══════════════════════ request bodies ══════════════════════

    public record BackupRequest(string? TypeKey, string? Destination);
}
