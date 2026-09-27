using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Making, changing and retiring accounts in the chart (27 Sep).
///
/// The Account List screen always had "New account", "Edit" and "Delete"
/// buttons, but the API only ever READ the chart (GET accounting/coa): each
/// button showed a success toast and saved nothing. The owner: "all account
/// section dynamic ... no dummy data, all from DB". These are the three writes.
///
/// RULES, so the books can never be broken from this screen:
///   * A code is digits, unique, and starts with its parent's first digit (the
///     chart is hierarchical: 1xxx assets, 2xxx liabilities, 3xxx capital,
///     4xxx revenue, 5xxx expenses). A child takes its parent's account type
///     group, so a liability never ends up filed under assets.
///   * A GROUP account is a heading: it can hold children, never postings.
///   * Once an account has postings (journal lines), its code and type are
///     fixed and it cannot turn into a group -- the ledger already reads it.
///   * The accounts the system posts to BY CODE (cash, banks, receivables,
///     inventory, payables, tax, sales, returns, staff and logistics payables,
///     the FS/margin reserves, salaries) can be renamed but never deleted,
///     switched off or re-coded -- a missing one stops invoices, purchases and
///     payroll from posting.
///   * Delete removes an account nothing has ever touched; one with history is
///     switched off instead (it leaves the pickers, keeps its ledger).
/// </summary>
[Route("api/accounting")]
[ApiController]
[Authorize(Roles = "super-admin,accountant")]
[Authorize(Policy = "perm:ledger.manage")]
public class ChartOfAccountsController : ApiControllerBase
{
    public ChartOfAccountsController(AppDbContext db, IConfiguration cfg,
        ILogger<ChartOfAccountsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /* Posted to by code somewhere in the API (Services/LedgerPosting.cs,
       PurchasesController, AccountingController, StaffLedgerController ...). */
    private static readonly HashSet<string> SystemCodes = new()
    {
        "1101", "1102", "1110", "1111", "1112", "1113", "1120", "1121", "1130", "1140",
        "2101", "2110", "2111", "2120", "2140", "2150", "2160", "2161", "2162",
        "4001", "4002", "5001", "5101",
    };

    /// <summary>Everything the New/Edit account form needs to offer real choices.</summary>
    [HttpGet("accounts/lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                types = await _db.AccountTypes.AsNoTracking()
                    .OrderBy(t => t.GroupId).ThenBy(t => t.AccountTypeId)
                    .Select(t => new
                    {
                        id = t.AccountTypeId, name = t.TypeName, groupId = t.GroupId,
                        group = t.Group.GroupName, isDebitNormal = t.IsDebitNormal
                    })
                    .ToListAsync(),
                groups = await _db.Accounts.AsNoTracking()
                    .Where(a => a.IsGroup && a.IsActive)
                    .OrderBy(a => a.AccountCode)
                    .Select(a => new
                    {
                        id = a.AccountId, code = a.AccountCode, name = a.AccountName,
                        typeId = a.AccountTypeId, groupId = a.AccountType.GroupId
                    })
                    .ToListAsync(),
                systemCodes = SystemCodes.OrderBy(c => c)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the account form's choices");
        }
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> Create([FromBody] AccountRequest body)
    {
        try
        {
            var code = (body.Code ?? "").Trim();
            var problem = await Validate(body, null, code);
            if (problem is not null) return BadRequest(new { message = problem });

            var acc = new Account
            {
                AccountCode = code,
                AccountName = body.Name!.Trim(),
                ParentAccountId = body.ParentId,
                AccountTypeId = body.AccountTypeId,
                IsGroup = body.IsGroup,
                OpeningBalance = body.IsGroup ? 0 : body.OpeningBalance,
                CurrencyCode = "PKR",
                IsActive = true
            };
            _db.Accounts.Add(acc);
            await _db.SaveChangesAsync();
            await Log("ACCOUNT_CREATED", "Account", acc.AccountCode, acc.AccountName, 2);
            return Ok(new { id = acc.AccountId, message = $"{acc.AccountCode} {acc.AccountName} added to the chart." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "add the account");
        }
    }

    [HttpPut("accounts/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AccountRequest body)
    {
        try
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.AccountId == id);
            if (acc is null) return NotFound(new { message = "No such account." });

            var code = (body.Code ?? acc.AccountCode).Trim();
            var problem = await Validate(body, acc, code);
            if (problem is not null) return BadRequest(new { message = problem });

            acc.AccountName = body.Name!.Trim();
            acc.AccountCode = code;
            acc.ParentAccountId = body.ParentId;
            acc.AccountTypeId = body.AccountTypeId;
            acc.IsGroup = body.IsGroup;
            if (!body.IsGroup) acc.OpeningBalance = body.OpeningBalance;
            if (body.IsActive is not null)
            {
                if (!body.IsActive.Value && SystemCodes.Contains(acc.AccountCode))
                    return BadRequest(new { message = $"{acc.AccountCode} is posted to by the system and cannot be switched off." });
                acc.IsActive = body.IsActive.Value;
            }
            await _db.SaveChangesAsync();
            await Log("ACCOUNT_EDITED", "Account", acc.AccountCode, acc.AccountName, 2);
            return Ok(new { id, message = $"{acc.AccountCode} {acc.AccountName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the account");
        }
    }

    /// <summary>Deletes an account nothing has touched; switches off one with history.</summary>
    [HttpDelete("accounts/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var acc = await _db.Accounts.FirstOrDefaultAsync(a => a.AccountId == id);
            if (acc is null) return NotFound(new { message = "No such account." });
            if (SystemCodes.Contains(acc.AccountCode))
                return BadRequest(new { message = $"{acc.AccountCode} {acc.AccountName} is posted to by the system -- it can be renamed, never removed." });
            if (await _db.Accounts.AnyAsync(a => a.ParentAccountId == id && a.IsActive))
                return BadRequest(new { message = $"{acc.AccountName} still has active accounts under it. Move or remove them first." });

            if (await HasHistory(id) || await _db.Accounts.AnyAsync(a => a.ParentAccountId == id))
            {
                acc.IsActive = false;
                await _db.SaveChangesAsync();
                await Log("ACCOUNT_DEACTIVATED", "Account", acc.AccountCode, acc.AccountName, 2);
                return Ok(new { id, deactivated = true, message = $"{acc.AccountName} has history, so it was switched off instead of deleted. Its ledger stays." });
            }

            _db.Accounts.Remove(acc);
            await _db.SaveChangesAsync();
            await Log("ACCOUNT_DELETED", "Account", acc.AccountCode, acc.AccountName, 3);
            return Ok(new { id, deactivated = false, message = $"{acc.AccountCode} {acc.AccountName} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "remove the account");
        }
    }

    // ── rules ────────────────────────────────────────────────────────────

    private async Task<bool> HasHistory(int id) =>
        await _db.JournalEntryLines.AnyAsync(l => l.AccountId == id)
        || await _db.Vouchers.AnyAsync(v => v.CashBankAccountId == id)
        || await _db.Expenses.AnyAsync(e => e.ExpenseAccountId == id || e.PaidFromAccountId == id)
        || await _db.PurchaseOrderItems.AnyAsync(i => i.DutyAccountId == id)
        /* Every foreign key into "Account" except the purchase line's is ON
           DELETE CASCADE (HANDOFF trap 24): anything that points at the account
           must count as history here, or deleting it would silently delete
           those rows too. The full list, checked 27 Sep: Account.Parent,
           BankReconciliation, Expense (x2), JournalEntryLine, Voucher,
           PurchaseOrderItem. */
        || await _db.BankReconciliations.AnyAsync(b => b.AccountId == id);

    private async Task<string?> Validate(AccountRequest b, Account? existing, string code)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 100) return "Give the account a name (up to 100 characters).";
        if (code.Length is < 3 or > 15 || !code.All(char.IsDigit)) return "The code is 3 to 15 digits.";
        if (await _db.Accounts.AnyAsync(a => a.AccountCode == code && (existing == null || a.AccountId != existing.AccountId)))
            return $"Code {code} is already used.";

        var type = await _db.AccountTypes.AsNoTracking().FirstOrDefaultAsync(t => t.AccountTypeId == b.AccountTypeId);
        if (type is null) return "Pick the account's type.";

        if (b.ParentId is not null)
        {
            if (existing is not null && b.ParentId == existing.AccountId) return "An account cannot sit under itself.";
            var parent = await _db.Accounts.AsNoTracking().Include(a => a.AccountType)
                .FirstOrDefaultAsync(a => a.AccountId == b.ParentId);
            if (parent is null || !parent.IsGroup) return "The parent must be a group account.";
            if (parent.AccountType.GroupId != type.GroupId)
                return $"{parent.AccountName} is under {await GroupName(parent.AccountType.GroupId)}; a {type.TypeName} account cannot go there.";
            if (parent.AccountCode[0] != code[0])
                return $"Accounts under {parent.AccountCode} must start with {parent.AccountCode[0]}.";
            if (existing is not null && await IsDescendant(b.ParentId.Value, existing.AccountId))
                return "That would put the account under one of its own children.";
        }

        if (existing is not null)
        {
            var isSystem = SystemCodes.Contains(existing.AccountCode);
            var used = await HasHistory(existing.AccountId);
            if ((isSystem || used) && code != existing.AccountCode)
                return $"{existing.AccountCode} {(isSystem ? "is posted to by the system" : "has postings")}, so its code is fixed.";
            if ((isSystem || used) && b.AccountTypeId != existing.AccountTypeId)
                return $"{existing.AccountCode} {(isSystem ? "is posted to by the system" : "has postings")}, so its type is fixed.";
            if (b.IsGroup && !existing.IsGroup && used)
                return "An account with postings cannot become a group heading.";
            if (!b.IsGroup && existing.IsGroup && await _db.Accounts.AnyAsync(a => a.ParentAccountId == existing.AccountId))
                return "This group still has accounts under it, so it must stay a group.";
        }
        return null;
    }

    private async Task<string> GroupName(int groupId) =>
        await _db.AccountGroups.Where(g => g.GroupId == groupId).Select(g => g.GroupName).FirstOrDefaultAsync() ?? "another group";

    private async Task<bool> IsDescendant(int candidateId, int ancestorId)
    {
        int? cur = candidateId;
        for (var guard = 0; cur is not null && guard < 20; guard++)
        {
            if (cur == ancestorId) return true;
            cur = await _db.Accounts.Where(a => a.AccountId == cur).Select(a => a.ParentAccountId).FirstOrDefaultAsync();
        }
        return false;
    }

    public record AccountRequest(
        string? Code, string? Name, int AccountTypeId, int? ParentId, bool IsGroup,
        decimal OpeningBalance, bool? IsActive);
}
