using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Staff Ledgers -- what the business owes each member of staff, and what each
/// owes back, posted to the main books.
///
/// ─────────────────────────────── THE ACCOUNTING ───────────────────────────────
///
/// One liability, 2140 Staff Payables, split by person through
/// "JournalEntryLine"."StaffId" (migrations 30 and 31 explain why one account
/// and not one per head). Every row on a staff ledger is a posted journal entry:
///
///   Salary due         Dr 5101 Salary Expense      Cr 2140 (staff)
///   Bonus / allowance  Dr 5101 Salary Expense      Cr 2140 (staff)
///   Salary paid        Dr 2140 (staff)             Cr the cash or bank it was paid from
///   Advance            Dr 2140 (staff)             Cr the cash or bank it was paid from
///   Deduction          Dr 2140 (staff)             Cr 5101 Salary Expense  (a fine or a
///                                                  short-paid day reduces the cost)
///   Adjustment         either side, against any account the accountant picks
///
/// So the ledger's balance is in the liability's own sense: CREDIT = salary we
/// still owe them, DEBIT = an advance they still owe us. The screen and the
/// PDF say which in words.
///
/// ─────────────────────────────── WHO IS ON IT ─────────────────────────────────
///
/// Everybody on the payroll: every login that is an employee (their row is made
/// the first time this screen is opened -- EnsureStaffRows) and every driver or
/// helper opened here WITHOUT a login. See Models/StaffMember.cs and migration
/// 31 for why the second kind can never sign in.
///
/// Super Admin and Accountant only, by ROLE -- the order desk and the sales
/// reps never see anybody's salary, their own included.
///
/// Controller-only by design; request records at the foot; try/catch + Fail().
/// </summary>
[Route("api/ledgers/staff")]
[ApiController]
[Authorize(Roles = "super-admin,accountant")]
public class StaffLedgerController : ApiControllerBase
{
    public StaffLedgerController(AppDbContext db, IConfiguration cfg,
        ILogger<StaffLedgerController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    private const int PostedStatusId = 2;

    /// <summary>The kinds of row the "+" can add, and which side of the staff account each one sits on.</summary>
    private static readonly Dictionary<string, (string Label, string Side, string DefaultCounter)> RowTypes = new()
    {
        ["salary"] = ("Salary due", "credit", LedgerPosting.SalaryExpenseCode),
        ["bonus"] = ("Bonus / allowance", "credit", LedgerPosting.SalaryExpenseCode),
        ["payment"] = ("Salary paid", "debit", LedgerPosting.CashOnHandCode),
        ["advance"] = ("Advance", "debit", LedgerPosting.CashOnHandCode),
        ["deduction"] = ("Deduction", "debit", LedgerPosting.SalaryExpenseCode),
        ["adjustment"] = ("Adjustment", "either", LedgerPosting.CashOnHandCode),
    };

    /// <summary>
    /// Makes sure every employee with a login has a staff row -- so a new
    /// salesman created at Setup > Users is on this screen the next time it is
    /// opened, without anybody remembering to add him. EMP-000 is the system
    /// service account, not a person.
    /// </summary>
    private async Task EnsureStaffRows()
    {
        var missing = await _db.Employees.AsNoTracking()
            .Where(e => e.EmployeeCode != "EMP-000" && !_db.StaffMembers.Any(s => s.UserId == e.UserId))
            .Select(e => new { e.UserId, e.EmployeeCode, e.JoinedOn, e.User.FullName, e.User.Phone, e.User.IsActive, role = e.User.Role.RoleKey })
            .ToListAsync();
        if (missing.Count == 0) return;

        var cats = await _db.StaffCategories.ToListAsync();
        int CategoryFor(string role)
        {
            var name = role switch
            {
                "sales" => "Salesman", "order-dept" => "Order Desk", "accountant" => "Office", _ => "Management"
            };
            var hit = cats.FirstOrDefault(c => c.CategoryName == name) ?? cats.FirstOrDefault();
            if (hit is not null) return hit.StaffCategoryId;
            var made = new StaffCategory { CategoryName = name };
            _db.StaffCategories.Add(made);
            _db.SaveChanges();
            cats.Add(made);
            return made.StaffCategoryId;
        }

        foreach (var m in missing)
        {
            var code = m.EmployeeCode;
            if (await _db.StaffMembers.AnyAsync(s => s.StaffCode == code)) code = await NextStaffCode();
            _db.StaffMembers.Add(new StaffMember
            {
                StaffCode = code, FullName = m.FullName, Phone = m.Phone,
                StaffCategoryId = CategoryFor(m.role), UserId = m.UserId,
                JoinedOn = m.JoinedOn, IsActive = m.IsActive, CreatedAt = Now()
            });
            await _db.SaveChangesAsync();
        }
    }

    private async Task<string> NextStaffCode()
    {
        const string prefix = "ST-";
        var used = await _db.StaffMembers.Where(s => s.StaffCode.StartsWith(prefix)).Select(s => s.StaffCode).ToListAsync();
        var next = used.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
        return $"{prefix}{next:0000}";
    }

    // ══════════════════════════════════════════════════════════════════
    //  INDEX
    // ══════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> GetStaff(
        [FromQuery] string? q, [FromQuery] int? categoryId, [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        try
        {
            await EnsureStaffRows();
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 25;

            var acc = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.StaffPayablesCode) ?? 0;

            var rows = _db.StaffMembers.AsNoTracking().AsQueryable();
            if (!includeInactive) rows = rows.Where(s => s.IsActive);
            if (categoryId is not null) rows = rows.Where(s => s.StaffCategoryId == categoryId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(s => s.FullName.ToLower().Contains(term) || s.StaffCode.ToLower().Contains(term)
                                    || (s.Phone != null && s.Phone.Contains(term)));
            }

            var shaped = rows.Select(s => new
            {
                id = s.StaffId,
                code = s.StaffCode,
                name = s.FullName,
                categoryId = s.StaffCategoryId,
                category = s.Category.CategoryName,
                phone = s.Phone,
                monthlySalary = s.MonthlySalary,
                hasLogin = s.UserId != null,
                role = s.UserId == null ? null
                    : _db.Users.Where(u => u.UserId == s.UserId).Select(u => u.Role.RoleName).FirstOrDefault(),
                isActive = s.IsActive,
                balance = s.OpeningBalance + (_db.JournalEntryLines
                    .Where(l => l.StaffId == s.StaffId && l.AccountId == acc && l.Entry.StatusId == PostedStatusId)
                    .Sum(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m)
            });

            var total = await shaped.CountAsync();
            var totalPayable = await shaped.SumAsync(x => (decimal?)x.balance) ?? 0m;
            var items = await shaped.OrderBy(x => x.name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            return Ok(new { total, page, pageSize, totalPayable, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the staff ledgers");
        }
    }

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                categories = await CategoryList(),
                rowTypes = RowTypes.Select(t => new { key = t.Key, label = t.Value.Label, side = t.Value.Side, defaultAccountCode = t.Value.DefaultCounter }),
                /* The other side of a row: any posting account except 2140
                   itself. The screen narrows it by type -- cash and bank for a
                   payment or an advance, expense for salary. */
                accounts = await _db.Accounts.AsNoTracking()
                    .Where(a => a.IsActive && !a.IsGroup && a.AccountCode != LedgerPosting.StaffPayablesCode)
                    .OrderBy(a => a.AccountCode)
                    .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName, type = a.AccountType.TypeName, group = a.AccountType.Group.GroupName })
                    .ToListAsync(),
                /* Logins that are not yet on a staff row -- for linking an
                   existing user to a person added here by hand. */
                unlinkedUsers = await _db.Employees.AsNoTracking()
                    .Where(e => e.EmployeeCode != "EMP-000" && !_db.StaffMembers.Any(s => s.UserId == e.UserId))
                    .Select(e => new { id = e.UserId, name = e.User.FullName, role = e.User.Role.RoleName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the staff ledger lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  OPEN / EDIT A STAFF ACCOUNT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Adds a member of staff WITHOUT a login -- a driver, a helper. Category
    /// required. Nothing here creates a "User": there is no email, password or
    /// role to sign in with, which is the point.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateStaff([FromBody] StaffRequest body)
    {
        try
        {
            var error = await ValidateStaff(body, null);
            if (error is not null) return BadRequest(new { message = error });

            var s = new StaffMember
            {
                StaffCode = string.IsNullOrWhiteSpace(body.Code) ? await NextStaffCode() : body.Code.Trim().ToUpperInvariant(),
                FullName = body.FullName!.Trim(),
                Phone = Clean(body.Phone),
                Cnic = Clean(body.Cnic),
                StaffCategoryId = body.CategoryId!.Value,
                UserId = body.UserId,
                MonthlySalary = Math.Round(body.MonthlySalary ?? 0m, 2),
                OpeningBalance = Math.Round(body.OpeningBalance ?? 0m, 2),
                JoinedOn = body.JoinedOn ?? Today(),
                IsActive = true,
                Notes = Clean(body.Notes),
                CreatedAt = Now()
            };
            _db.StaffMembers.Add(s);
            await _db.SaveChangesAsync();
            await Log("STAFF_ADDED", "StaffMember", s.StaffCode, s.FullName, 1);

            return Ok(new { id = s.StaffId, code = s.StaffCode, message = $"{s.FullName} added as {s.StaffCode}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "add the member of staff");
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateStaff(int id, [FromBody] StaffRequest body)
    {
        try
        {
            var s = await _db.StaffMembers.FirstOrDefaultAsync(x => x.StaffId == id);
            if (s is null) return NotFound(new { message = "No such member of staff." });
            var error = await ValidateStaff(body, id);
            if (error is not null) return BadRequest(new { message = error });

            s.FullName = body.FullName!.Trim();
            s.Phone = Clean(body.Phone);
            s.Cnic = Clean(body.Cnic);
            s.StaffCategoryId = body.CategoryId!.Value;
            s.MonthlySalary = Math.Round(body.MonthlySalary ?? 0m, 2);
            s.OpeningBalance = Math.Round(body.OpeningBalance ?? s.OpeningBalance, 2);
            s.JoinedOn = body.JoinedOn ?? s.JoinedOn;
            s.Notes = Clean(body.Notes);
            if (body.IsActive is bool active) s.IsActive = active;
            if (body.UserId is not null || body.UnlinkUser == true) s.UserId = body.UnlinkUser == true ? null : body.UserId;
            await _db.SaveChangesAsync();
            await Log("STAFF_UPDATED", "StaffMember", s.StaffCode, s.FullName, 1);
            return Ok(new { id, message = $"{s.FullName} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save staff {id}");
        }
    }

    private async Task<string?> ValidateStaff(StaffRequest b, int? existingId)
    {
        var name = (b.FullName ?? "").Trim();
        if (name.Length is < 2 or > 120) return "A name is 2 to 120 characters.";
        if (b.CategoryId is null || !await _db.StaffCategories.AnyAsync(c => c.StaffCategoryId == b.CategoryId))
            return "Pick a staff category -- every staff account needs one.";
        if (b.MonthlySalary is < 0) return "A salary cannot be negative.";
        if (!string.IsNullOrWhiteSpace(b.Code))
        {
            var code = b.Code.Trim().ToUpperInvariant();
            if (code.Length > 20) return "A staff code is at most 20 characters.";
            if (await _db.StaffMembers.AnyAsync(s => s.StaffCode == code && s.StaffId != existingId))
                return $"Staff code {code} is already in use.";
        }
        if (b.UserId is int uid)
        {
            if (!await _db.Employees.AnyAsync(e => e.UserId == uid)) return "That login is not an employee.";
            if (await _db.StaffMembers.AnyAsync(s => s.UserId == uid && s.StaffId != existingId))
                return "That login is already linked to another member of staff.";
        }
        return null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENT
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}/statement")]
    public async Task<IActionResult> GetStatement(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var s = await BuildStatement(id, from, to);
            return s is null ? NotFound(new { message = "No such member of staff." }) : Ok(s);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the staff statement {id}");
        }
    }

    public sealed record StaffRow(
        string key, DateOnly? date, string kind, string particulars, string? account,
        decimal debit, decimal credit, decimal balance, int? entryId, string? entryNo,
        bool reversed, bool canReverse);

    public sealed record StaffStatement(
        object staff, DateOnly from, DateOnly to, decimal balanceBroughtForward,
        List<StaffRow> rows, int entryCount, decimal totalDebit, decimal totalCredit, decimal closingBalance);

    private async Task<StaffStatement?> BuildStatement(int id, DateOnly? from, DateOnly? to)
    {
        var s = await _db.StaffMembers.AsNoTracking()
            .Where(x => x.StaffId == id)
            .Select(x => new
            {
                id = x.StaffId, code = x.StaffCode, name = x.FullName, phone = x.Phone, cnic = x.Cnic,
                categoryId = x.StaffCategoryId, category = x.Category.CategoryName,
                monthlySalary = x.MonthlySalary, openingBalance = x.OpeningBalance,
                joinedOn = x.JoinedOn, isActive = x.IsActive, notes = x.Notes, userId = x.UserId,
                login = x.UserId == null ? null : _db.Users.Where(u => u.UserId == x.UserId)
                    .Select(u => u.Email ?? u.FullName).FirstOrDefault(),
                role = x.UserId == null ? null : _db.Users.Where(u => u.UserId == x.UserId)
                    .Select(u => u.Role.RoleName).FirstOrDefault()
            })
            .FirstOrDefaultAsync();
        if (s is null) return null;

        var today = Today();
        var end = to ?? today;
        var start = from ?? new DateOnly(today.Year, today.Month, 1).AddMonths(-2);
        if (start > end) (start, end) = (end, start);

        var acc = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.StaffPayablesCode) ?? 0;
        var mine = _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.StaffId == id && l.AccountId == acc && l.Entry.StatusId == PostedStatusId);

        var before = await mine.Where(l => l.Entry.EntryDate < start)
            .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;
        var bf = s.openingBalance + before;

        var lines = await mine
            .Where(l => l.Entry.EntryDate >= start && l.Entry.EntryDate <= end)
            .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.EntryId)
            .Select(l => new
            {
                l.LineId, l.EntryId, l.DebitAmount, l.CreditAmount, l.Description,
                date = l.Entry.EntryDate, entryNo = l.Entry.EntryNo, narration = l.Entry.Narration,
                reference = l.Entry.ReferenceNo, reversedBy = l.Entry.ReversedByEntryId,
                isMirror = l.Entry.Reverses.Any(),
                /* The other side of the entry -- "paid from HBL", "Salary Expense". */
                other = l.Entry.JournalEntryLines.Where(o => o.LineId != l.LineId)
                    .Select(o => o.Account.AccountName).FirstOrDefault()
            })
            .ToListAsync();

        var running = bf;
        var rows = new List<StaffRow>
        {
            new("bf", start, "opening", "Balance B/F", null, bf < 0 ? -bf : 0, bf > 0 ? bf : 0, bf, null, null, false, false)
        };
        foreach (var l in lines)
        {
            running += l.CreditAmount - l.DebitAmount;
            var kind = l.isMirror ? "reversal"
                : (l.reference ?? "").StartsWith("STAFF ") || (l.reference ?? "").StartsWith("SALARY ") ? "manual" : "journal";
            rows.Add(new StaffRow($"l{l.LineId}", l.date, kind,
                string.IsNullOrWhiteSpace(l.narration) ? (l.Description ?? "") : l.narration, l.other,
                l.DebitAmount, l.CreditAmount, running, l.EntryId, l.entryNo,
                l.reversedBy is not null, kind == "manual" && l.reversedBy is null));
        }

        return new StaffStatement(s, start, end, bf, rows, rows.Count,
            rows.Sum(r => r.debit), rows.Sum(r => r.credit), running);
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE "+" ROW
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A row on a staff ledger: date, type, description, a debit or a credit,
    /// and the account on the other side (paid-from cash or bank for money
    /// going out; Salary Expense by default for salary). Posted immediately.
    /// </summary>
    [HttpPost("{id:int}/entries")]
    public async Task<IActionResult> AddEntry(int id, [FromBody] StaffRowRequest body)
    {
        try
        {
            var s = await _db.StaffMembers.AsNoTracking().FirstOrDefaultAsync(x => x.StaffId == id);
            if (s is null) return NotFound(new { message = "No such member of staff." });

            var type = (body.Type ?? "").Trim().ToLowerInvariant();
            if (!RowTypes.TryGetValue(type, out var spec))
                return BadRequest(new { message = $"Pick a row type: {string.Join(", ", RowTypes.Values.Select(v => v.Label))}." });

            var debit = Math.Round(body.Debit ?? 0m, 2);
            var credit = Math.Round(body.Credit ?? 0m, 2);
            if (debit < 0 || credit < 0) return BadRequest(new { message = "Debit and credit cannot be negative." });
            if ((debit > 0) == (credit > 0)) return BadRequest(new { message = "Enter a debit OR a credit -- one of them, above zero." });
            if (spec.Side == "credit" && debit > 0)
                return BadRequest(new { message = $"{spec.Label} is a credit -- it is money the business owes {s.FullName}." });
            if (spec.Side == "debit" && credit > 0)
                return BadRequest(new { message = $"{spec.Label} is a debit -- it is money paid to, or kept from, {s.FullName}." });
            var amount = debit > 0 ? debit : credit;

            var staffAcc = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.StaffPayablesCode);
            if (staffAcc is null) return BadRequest(new { message = "Account 2140 Staff Payables is missing -- run migration 30." });

            int counter;
            if (body.AccountId is int chosen)
            {
                if (!await _db.Accounts.AnyAsync(a => a.AccountId == chosen && !a.IsGroup && a.IsActive && a.AccountId != staffAcc))
                    return BadRequest(new { message = "Pick an account for the other side that can take a posting." });
                counter = chosen;
            }
            else
            {
                counter = await LedgerPosting.AccountIdAsync(_db, spec.DefaultCounter)
                          ?? throw new InvalidOperationException($"Account {spec.DefaultCounter} is missing.");
            }

            var description = (body.Description ?? "").Trim();
            if (description.Length > 250) return BadRequest(new { message = "Keep the description under 250 characters." });
            var label = description.Length > 0 ? $"{spec.Label} -- {description}" : spec.Label;

            var (entry, error) = await PostStaffRow(s, body.Date ?? Today(), label, debit, credit, staffAcc.Value, counter,
                $"STAFF {s.StaffCode}");
            if (entry is null) return BadRequest(new { message = error });

            await Log("STAFF_ROW_POSTED", "JournalEntry", entry.EntryNo,
                $"{s.StaffCode} {(debit > 0 ? "Dr" : "Cr")} {amount:N2} -- {label}", 2);
            return Ok(new { entryId = entry.EntryId, entryNo = entry.EntryNo, message = $"Posted as {entry.EntryNo}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the staff ledger row");
        }
    }

    private async Task<(JournalEntry? Entry, string? Error)> PostStaffRow(StaffMember s, DateOnly date, string label,
        decimal debit, decimal credit, int staffAcc, int counter, string reference)
    {
        var amount = debit > 0 ? debit : credit;
        var location = await _db.Locations.AsNoTracking()
            .Where(l => l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.LocationId)
            .Select(l => l.LocationId).FirstAsync();

        await using var tx = await _db.Database.BeginTransactionAsync();
        var result = await LedgerPosting.WriteEntryAsync(_db, date, "SALARY", location, reference,
            $"{label} -- {s.FullName}", CurrentUserId(), new[]
            {
                /* Debit on the staff account: money out to them (paid, advance,
                   deduction). Credit: money they have earned. */
                new LedgerPosting.Leg(debit > 0 ? staffAcc : counter, amount, 0m, label, StaffId: debit > 0 ? s.StaffId : null),
                new LedgerPosting.Leg(debit > 0 ? counter : staffAcc, 0m, amount, label, StaffId: debit > 0 ? null : s.StaffId)
            });
        if (result.Entry is null) { await tx.RollbackAsync(); return result; }
        await tx.CommitAsync();
        return result;
    }

    /// <summary>
    /// Salary due for a month, for every active member of staff with a monthly
    /// salary set -- one entry each, dated the last day of the month.
    /// IDEMPOTENT per person and month: the entry carries "SALARY {code}
    /// {yyyy-MM}" as its reference and a second run skips anybody who has one.
    /// </summary>
    [HttpPost("salary-run")]
    public async Task<IActionResult> SalaryRun([FromBody] SalaryRunRequest body)
    {
        try
        {
            if (!DateOnly.TryParseExact((body.Month ?? "") + "-01", "yyyy-MM-dd", out var first))
                return BadRequest(new { message = "Say which month, as yyyy-MM." });
            var last = first.AddMonths(1).AddDays(-1);
            var tag = $"{first:yyyy-MM}";

            var staffAcc = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.StaffPayablesCode);
            var expense = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.SalaryExpenseCode);
            if (staffAcc is null || expense is null) return BadRequest(new { message = "Accounts 2140 and 5101 must be in the chart." });

            var people = await _db.StaffMembers.AsNoTracking()
                .Where(s => s.IsActive && s.MonthlySalary > 0).OrderBy(s => s.FullName).ToListAsync();

            var posted = new List<string>();
            var skipped = new List<string>();
            foreach (var s in people)
            {
                var reference = $"SALARY {s.StaffCode} {tag}";
                if (await _db.JournalEntries.AnyAsync(e => e.ReferenceNo == reference && e.ReversedByEntryId == null))
                {
                    skipped.Add(s.FullName);
                    continue;
                }
                var (entry, error) = await PostStaffRow(s, last, $"Salary due {first:MMM yyyy}",
                    0m, s.MonthlySalary, staffAcc.Value, expense.Value, reference);
                if (entry is null) return BadRequest(new { message = $"{s.FullName}: {error}" });
                posted.Add(s.FullName);
            }

            await Log("SALARY_RUN", "JournalEntry", tag, $"{posted.Count} posted, {skipped.Count} already had it", 2);
            return Ok(new
            {
                posted = posted.Count, skipped = skipped.Count,
                message = posted.Count == 0
                    ? $"Nothing to post -- everyone with a salary already has {first:MMMM yyyy}."
                    : $"Salary due for {first:MMMM yyyy} posted for {posted.Count} {(posted.Count == 1 ? "person" : "people")}."
                      + (skipped.Count > 0 ? $" {skipped.Count} already had it." : "")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the month's salaries");
        }
    }

    /// <summary>Undo a row -- reversed by a mirror, never deleted.</summary>
    [HttpPost("{id:int}/entries/{entryId:int}/reverse")]
    public async Task<IActionResult> ReverseEntry(int id, int entryId, [FromBody] ReverseRequest? body)
    {
        try
        {
            var entry = await _db.JournalEntries.Include(e => e.JournalEntryLines)
                .FirstOrDefaultAsync(e => e.EntryId == entryId);
            if (entry is null || !entry.JournalEntryLines.Any(l => l.StaffId == id))
                return NotFound(new { message = "That row is not on this ledger." });
            if (!(entry.ReferenceNo ?? "").StartsWith("STAFF ") && !(entry.ReferenceNo ?? "").StartsWith("SALARY "))
                return BadRequest(new { message = "Only rows written on the staff ledger can be undone here." });
            if (entry.ReversedByEntryId is not null)
                return BadRequest(new { message = $"{entry.EntryNo} has already been reversed." });

            await using var tx = await _db.Database.BeginTransactionAsync();
            var (mirror, error) = await LedgerPosting.WriteEntryAsync(_db, Today(), "SALARY", entry.LocationId,
                entry.EntryNo, $"Reversal of {entry.EntryNo}" +
                (string.IsNullOrWhiteSpace(body?.Reason) ? "" : $" -- {body!.Reason!.Trim()}"),
                CurrentUserId(),
                entry.JournalEntryLines.OrderBy(l => l.LineNo)
                    .Select(l => new LedgerPosting.Leg(l.AccountId, l.CreditAmount, l.DebitAmount,
                        $"Reversal: {l.Description}", l.PartyUserId, l.StaffId)).ToList());
            if (mirror is null) return BadRequest(new { message = error });
            entry.ReversedByEntryId = mirror.EntryId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("STAFF_ROW_REVERSED", "JournalEntry", entry.EntryNo, mirror.EntryNo, 2);
            return Ok(new { entryId = mirror.EntryId, message = $"{entry.EntryNo} reversed by {mirror.EntryNo}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reverse entry {entryId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PDF
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}/statement/pdf")]
    public async Task<IActionResult> RenderPdf(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] bool attachment = false)
    {
        try
        {
            var built = await BuildPdf(id, from, to);
            if (built is null) return NotFound(new { message = "No such member of staff." });
            Response.Headers.ContentDisposition = $"{(attachment ? "attachment" : "inline")}; filename=\"{built.Value.FileName}\"";
            return File(built.Value.Bytes, "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the staff statement {id}");
        }
    }

    [HttpPost("{id:int}/statement/pdf")]
    public async Task<IActionResult> ArchivePdf(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var built = await BuildPdf(id, from, to);
            if (built is null) return NotFound(new { message = "No such member of staff." });
            var (bytes, fileName, key, code) = built.Value;
            var stored = await DocumentArchive.StoreAsync(_db, _cfg, "staff-ledger", key, code,
                fileName, bytes, CurrentUserId(), "statements");
            await Log("STATEMENT_ARCHIVED", "staff-ledger", code, stored.PdfUrl, 1);
            return Ok(new
            {
                archived = true, fileId = stored.FileId, fileName = stored.FileName,
                pdfUrl = stored.PdfUrl, isDeliverable = stored.Deliverable, bytes = stored.Bytes,
                message = $"Staff statement {code} saved to the document store."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the staff statement {id}");
        }
    }

    private async Task<(byte[] Bytes, string FileName, string Key, string Code)?> BuildPdf(int id, DateOnly? from, DateOnly? to)
    {
        var st = await BuildStatement(id, from, to);
        if (st is null) return null;
        var j = JsonSerializer.SerializeToElement(st.staff);
        string Str(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        decimal Dec(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

        var lines = new List<string> { Str("category") };
        if (Str("phone").Length > 0) lines.Add($"Phone {Str("phone")}");
        if (Str("role").Length > 0) lines.Add($"Signs in as {Str("role")}");

        var data = new LedgerStatementPdf.Data(
            Company: await DocumentBuilder.LetterHead(_db),
            Title: "Staff Statement",
            AccountCode: Str("code"),
            AccountName: Str("name"),
            AccountLines: lines.Where(x => x.Length > 0).ToList(),
            From: st.from, To: st.to,
            LimitLabel: "Monthly Salary",
            Limit: Dec("monthlySalary"),
            Lines: st.rows.Select(r => new LedgerStatementPdf.Line(
                r.date, r.account is null ? r.particulars : $"{r.particulars} · {r.account}",
                r.debit, r.credit, r.balance, Array.Empty<LedgerStatementPdf.Item>(),
                Emphasis: r.kind == "opening",
                Tag: r.kind == "reversal" ? "Reversal" : r.reversed ? "Reversed" : null)).ToList(),
            EntryCount: st.entryCount,
            TotalDebit: st.totalDebit,
            TotalCredit: st.totalCredit,
            Closing: st.closingBalance,
            ClosingLabel: st.closingBalance >= 0 ? "Payable to staff" : "Advance outstanding",
            Footnote: "Balance is in the payroll's own sense: positive is salary still owed, negative is an advance to be recovered.");

        var code = Str("code");
        var file = $"STAFF-{code}-{st.from:yyyyMMdd}-{st.to:yyyyMMdd}.pdf";
        foreach (var bad in Path.GetInvalidFileNameChars()) file = file.Replace(bad, '-');
        return (LedgerStatementPdf.Render(data), file, $"{id}:{st.from:yyyy-MM-dd}:{st.to:yyyy-MM-dd}", code);
    }

    // ══════════════════════════════════════════════════════════════════
    //  STAFF CATEGORIES -- inline add / rename / remove
    // ══════════════════════════════════════════════════════════════════

    private async Task<object> CategoryList() =>
        await _db.StaffCategories.AsNoTracking()
            .OrderBy(c => c.CategoryName)
            .Select(c => new
            {
                id = c.StaffCategoryId, name = c.CategoryName,
                inUse = _db.StaffMembers.Count(s => s.StaffCategoryId == c.StaffCategoryId)
            })
            .ToListAsync();

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        try { return Ok(await CategoryList()); }
        catch (Exception ex) { return Fail(ex, "load the staff categories"); }
    }

    [HttpPost("categories")]
    public async Task<IActionResult> AddCategory([FromBody] CategoryRequest body)
    {
        try
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 60) return BadRequest(new { message = "A category name is 2 to 60 characters." });
            if (await _db.StaffCategories.AnyAsync(c => c.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"There is already a category called {name}." });
            var c = new StaffCategory { CategoryName = name };
            _db.StaffCategories.Add(c);
            await _db.SaveChangesAsync();
            await Log("STAFF_CATEGORY_ADDED", "StaffCategory", name, null, 1);
            return Ok(new { id = c.StaffCategoryId, name, message = $"{name} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "add the staff category");
        }
    }

    [HttpPut("categories/{categoryId:int}")]
    public async Task<IActionResult> RenameCategory(int categoryId, [FromBody] CategoryRequest body)
    {
        try
        {
            var c = await _db.StaffCategories.FirstOrDefaultAsync(x => x.StaffCategoryId == categoryId);
            if (c is null) return NotFound(new { message = "No such category." });
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 60) return BadRequest(new { message = "A category name is 2 to 60 characters." });
            if (await _db.StaffCategories.AnyAsync(x => x.StaffCategoryId != categoryId && x.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"There is already a category called {name}." });
            var was = c.CategoryName;
            c.CategoryName = name;
            await _db.SaveChangesAsync();
            await Log("STAFF_CATEGORY_RENAMED", "StaffCategory", name, $"{was} -> {name}", 1);
            return Ok(new { id = categoryId, name, message = $"Renamed to {name}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "rename the staff category");
        }
    }

    [HttpDelete("categories/{categoryId:int}")]
    public async Task<IActionResult> RemoveCategory(int categoryId)
    {
        try
        {
            var c = await _db.StaffCategories.FirstOrDefaultAsync(x => x.StaffCategoryId == categoryId);
            if (c is null) return NotFound(new { message = "No such category." });
            var used = await _db.StaffMembers.CountAsync(s => s.StaffCategoryId == categoryId);
            if (used > 0)
                return BadRequest(new
                {
                    message = $"{c.CategoryName} is used by {used} {(used == 1 ? "person" : "people")}. " +
                              "Move them to another category first -- it cannot be removed while in use."
                });
            _db.StaffCategories.Remove(c);
            await _db.SaveChangesAsync();
            await Log("STAFF_CATEGORY_REMOVED", "StaffCategory", c.CategoryName, null, 2);
            return Ok(new { message = $"{c.CategoryName} removed." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "remove the staff category");
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ══════════════════════════ request bodies ══════════════════════════

    public record StaffRequest(
        string? FullName, int? CategoryId, string? Phone, string? Cnic, decimal? MonthlySalary,
        decimal? OpeningBalance, DateOnly? JoinedOn, string? Notes, string? Code,
        int? UserId, bool? UnlinkUser, bool? IsActive);

    public record StaffRowRequest(
        DateOnly? Date, string? Type, string? Description, decimal? Debit, decimal? Credit, int? AccountId);

    public record SalaryRunRequest(string? Month);

    public record ReverseRequest(string? Reason);

    public record CategoryRequest(string? Name);
}
