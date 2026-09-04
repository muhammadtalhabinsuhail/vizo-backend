using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Chart-of-accounts types and the groups they sit under.
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
public class AdminAccountTypesController : AdminControllerBase
{
    public AdminAccountTypesController(AppDbContext db, IConfiguration cfg, ILogger<AdminAccountTypesController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  ACCOUNT TYPES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("account-types")]
    public async Task<IActionResult> GetAccountTypes([FromQuery] string? group)
    {
        try
        {
            var q = _db.AccountTypes.AsQueryable();
            if (!string.IsNullOrWhiteSpace(group) && group != "all")
                q = q.Where(t => t.Group.GroupName == group);

            var rows = await q.OrderBy(t => t.AccountTypeId)
                .Select(t => new
                {
                    id = t.AccountTypeId,
                    name = t.TypeName,
                    groupId = t.GroupId,
                    group = t.Group.GroupName,
                    prefix = t.CodePrefix,
                    codeLength = t.CodeLength,
                    normalBalance = t.IsDebitNormal ? "debit" : "credit",
                    onBalanceSheet = t.Group.OnBalanceSheet,
                    isSystem = t.IsSystem,
                    accountCount = t.Accounts.Count,
                    /* The next code this type would mint, from the highest number
                       actually issued -- not a figure hardcoded in the UI. */
                    lastSequence = t.Accounts
                        .Where(a => a.AccountCode.StartsWith(t.CodePrefix))
                        .Count()
                })
                .ToListAsync();

            var counts = await _db.AccountTypes
                .GroupBy(t => t.Group.GroupName)
                .Select(g => new { group = g.Key, count = g.Count() })
                .ToListAsync();

            return Ok(new
            {
                items = rows.Select(r => new
                {
                    r.id, r.name, r.groupId, r.group, r.prefix, r.codeLength,
                    r.normalBalance, r.onBalanceSheet, r.isSystem, r.accountCount,
                    nextCode = r.prefix + (r.lastSequence + 1).ToString().PadLeft(r.codeLength, '0')
                }),
                groupCounts = counts,
                total = rows.Count
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/account-types");
        }
    }

    [HttpGet("account-groups")]
    public async Task<IActionResult> GetAccountGroups()
    {
        try
        {
            return Ok(await _db.AccountGroups.OrderBy(g => g.GroupId)
                    .Select(g => new { id = g.GroupId, name = g.GroupName, onBalanceSheet = g.OnBalanceSheet })
                    .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/account-groups");
        }
    }

    [HttpPut("account-types/{id:int}")]
    public async Task<IActionResult> UpdateAccountType(int id, [FromBody] AccountTypeRequest body)
    {
        try
        {
            var t = await _db.AccountTypes.FirstOrDefaultAsync(x => x.AccountTypeId == id);
            if (t is null) return NotFound(new { message = "Account type not found." });

            if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { message = "Name is required." });
            if (string.IsNullOrWhiteSpace(body.Prefix)) return BadRequest(new { message = "Code prefix is required." });
            if (body.CodeLength is < 1 or > 12) return BadRequest(new { message = "Code length must be 1 to 12." });

            var prefix = body.Prefix.Trim().ToUpperInvariant();
            if (await _db.AccountTypes.AnyAsync(x => x.CodePrefix.ToUpper() == prefix && x.AccountTypeId != id))
                return BadRequest(new { message = "Another type already uses that prefix." });

            t.TypeName = body.Name.Trim();
            t.CodePrefix = prefix;
            t.CodeLength = (short)body.CodeLength;
            if (!t.IsSystem) t.IsDebitNormal = body.NormalBalance == "debit";

            await _db.SaveChangesAsync();
            await Log("UPDATED", "AccountType", t.TypeName, null, 1);
            return Ok(new { message = $"{t.TypeName} updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/account-types/{id:int}");
        }
    }

    // ══════════════════════ request bodies ══════════════════════

    public record AccountTypeRequest(string Name, string Prefix, int CodeLength, string NormalBalance);
}