using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Document numbering series -- prefix, padding and next number per document.
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
public class AdminNumberingController : AdminControllerBase
{
    public AdminNumberingController(AppDbContext db, IConfiguration cfg, ILogger<AdminNumberingController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  DOCUMENT NUMBERING
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("document-series")]
    public async Task<IActionResult> GetDocumentSeries()
    {
        try
        {
            var rows = await _db.DocumentSeries.OrderBy(s => s.SeriesId)
                .Select(s => new
                {
                    id = s.SeriesId,
                    key = s.SeriesKey,
                    label = s.Label,
                    prefix = s.Prefix,
                    includeYear = s.IncludeYear,
                    padding = s.Padding,
                    nextNumber = s.NextNumber
                })
                .ToListAsync();

            /* The two-digit year the preview should show, taken from the company's
               fiscal calendar rather than hardcoded in the page. */
            var company = await _db.Companies.FirstOrDefaultAsync();
            var startMonth = company?.FiscalYearStartMonth ?? 1;
            var today = DateTime.UtcNow;
            var fiscalYear = today.Month >= startMonth ? today.Year + 1 : today.Year;

            return Ok(new { items = rows, yearSuffix = fiscalYear % 100 });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/document-series");
        }
    }

    /// <summary>Saves the whole grid in one go, the way the screen edits it.</summary>
    [HttpPut("document-series")]
    public async Task<IActionResult> UpdateDocumentSeries([FromBody] List<DocumentSeriesRequest> body)
    {
        try
        {
            if (body is null || body.Count == 0) return BadRequest(new { message = "Nothing to save." });

            var ids = body.Select(b => b.Id).ToList();
            var rows = await _db.DocumentSeries.Where(s => ids.Contains(s.SeriesId)).ToListAsync();

            foreach (var b in body)
            {
                var row = rows.FirstOrDefault(r => r.SeriesId == b.Id);
                if (row is null) continue;

                if (string.IsNullOrWhiteSpace(b.Prefix) || b.Prefix.Trim().Length > 6)
                    return BadRequest(new { message = $"{row.Label}: prefix must be 1 to 6 characters." });
                if (b.Padding is < 2 or > 8)
                    return BadRequest(new { message = $"{row.Label}: digits must be between 2 and 8." });
                if (b.NextNumber < 1)
                    return BadRequest(new { message = $"{row.Label}: next number must be 1 or more." });

                var prefix = b.Prefix.Trim().ToUpperInvariant();
                if (rows.Any(r => r.SeriesId != b.Id && r.Prefix.ToUpper() == prefix) ||
                    await _db.DocumentSeries.AnyAsync(r => r.SeriesId != b.Id && r.Prefix.ToUpper() == prefix
                                                           && !ids.Contains(r.SeriesId)))
                    return BadRequest(new { message = $"{row.Label}: the prefix {prefix} is already in use." });

                row.Prefix = prefix;
                row.IncludeYear = b.IncludeYear;
                row.Padding = (short)b.Padding;
                row.NextNumber = b.NextNumber;
            }

            await _db.SaveChangesAsync();
            await Log("UPDATED", "DocumentSeries", "numbering", $"{body.Count} series saved", 3);
            return Ok(new { message = "Numbering saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/document-series");
        }
    }

    // ══════════════════════ request bodies ══════════════════════

    public record DocumentSeriesRequest(int Id, string Prefix, bool IncludeYear, int Padding, int NextNumber);
}