using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The daily expense sheet: one date, one location, as many expenses as that
/// day had, one approval, one journal entry, one printed invoice.
///
/// ─────────────────────────── WHY IT EXISTS ─────────────────────────────────
///
/// The owner, 26 September: "Expense records display fine today. But every
/// expense gets its own invoice. We want one invoice per day holding all of
/// that day's expenses." Until now each expense was its own document -- its
/// own number, its own approval, its own journal entry and its own PDF -- so a
/// day with twelve small payments was twelve vouchers to print, twelve clicks
/// to approve and twelve entries in the ledger.
///
/// ─────────────────────────── HOW IT FITS ───────────────────────────────────
///
/// Every expense is still an "Expense" row, because every report reads those:
/// the P&amp;L's expense section comes from the journal, but the expense
/// report, the month-on-month jump report, the nightly insights and the AI
/// summary all read "Expense" and filter on POSTED. The sheet is the row above
/// them ("ExpenseSheet", migration 35), and a line's status always follows its
/// sheet's -- DRAFT while the day is being typed, POSTED once the accountant
/// approves it, REVERSED once that is undone -- so those reports keep telling
/// the truth without changing.
///
/// Approval writes ONE journal entry: a debit per line to its expense head,
/// and one credit per cash or bank account the day was paid from. Every line
/// gets that entry's id in "Expense"."EntryId", which is what stops the entry
/// being deleted on its own (AccountingController.DeleteJournalEntry) and what
/// the old per-expense screen shows as "Linked journal entry".
///
/// ─────────────────────────── WHO MAY DO WHAT ───────────────────────────────
///
/// Entering: "expenses.manage" -- the right that has always governed the
/// Expenses screen (Accountant and Super Admin today, editable at Setup).
/// Approving and reversing: the Accountant policy, which is what the old
/// per-expense approve and reverse actions were guarded by. Both are required
/// for those two, so a role given only the entering right can type a day but
/// not post it.
///
/// Controller-only by design, like the rest of the API: no DTOs, no services.
/// </summary>
[Route("api/expense-sheets")]
[ApiController]
[Authorize(Policy = "perm:expenses.manage")]
public class ExpenseSheetsController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public ExpenseSheetsController(AppDbContext db, IConfiguration cfg,
        ILogger<ExpenseSheetsController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    private const string Draft = "DRAFT";
    private const string Posted = "POSTED";
    private const string Reversed = "REVERSED";
    private const string DocKind = "expense-sheet";

    /* A day with more lines than this is a data-entry accident, not a day. */
    private const int MaxLines = 300;

    // ══════════════════════════════════════════════════════════════════
    //  THE LIST OF DAYS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Day sheets, newest first. Every figure -- count, money, drafts waiting --
    /// is over the whole filter, not the page on screen.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? locationId, [FromQuery] string? status, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 25;

            var rows = _db.ExpenseSheets.AsNoTracking().AsQueryable();
            if (from is not null) rows = rows.Where(s => s.SheetDate >= from);
            if (to is not null) rows = rows.Where(s => s.SheetDate <= to);
            if (locationId is > 0) rows = rows.Where(s => s.LocationId == locationId);
            if (!string.IsNullOrWhiteSpace(status))
            {
                var key = status.Trim().ToUpperInvariant();
                rows = rows.Where(s => s.Status.StatusKey == key);
            }
            if (!string.IsNullOrWhiteSpace(q))
            {
                /* A sheet number, or anything on one of its lines -- "who did
                   we pay K-Electric through last month" is a vendor search. */
                var term = q.Trim().ToLower();
                rows = rows.Where(s => s.SheetNo.ToLower().Contains(term)
                    || s.Lines.Any(l => l.VendorName.ToLower().Contains(term)
                                     || (l.Description ?? "").ToLower().Contains(term)
                                     || l.CategoryName.ToLower().Contains(term)));
            }

            var count = await rows.CountAsync();
            var drafts = await rows.CountAsync(s => s.Status.StatusKey == Draft);

            /* The money over the whole filter, read off the lines. A line
               counts unless it was undone on its own before sheets existed --
               see Counts() for the one rule. */
            var ids = rows.Select(s => s.SheetId);
            var money = await _db.Expenses.AsNoTracking()
                .Where(l => l.SheetId != null && ids.Contains(l.SheetId.Value))
                .Where(l => l.Sheet!.Status.StatusKey != Reversed
                            && (l.Status.StatusKey == Draft || l.Status.StatusKey == Posted))
                .SumAsync(l => (decimal?)l.Amount) ?? 0m;

            var items = await rows
                .OrderByDescending(s => s.SheetDate)
                .ThenBy(s => s.Location.LocationName)
                .ThenByDescending(s => s.SheetId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(s => new
                {
                    id = s.SheetId,
                    sheetNo = s.SheetNo,
                    sheetDate = s.SheetDate,
                    locationId = s.LocationId,
                    location = s.Location.LocationName,
                    status = s.Status.StatusKey,
                    lines = s.Lines.Count(l => l.Status.StatusKey != "REJECTED" && l.Status.StatusKey != "CANCELLED"
                                               && (l.Status.StatusKey != Reversed || s.Status.StatusKey == Reversed)),
                    total = s.Lines
                        .Where(l => l.Status.StatusKey != "REJECTED" && l.Status.StatusKey != "CANCELLED"
                                    && (l.Status.StatusKey != Reversed || s.Status.StatusKey == Reversed))
                        .Sum(l => (decimal?)l.Amount) ?? 0m,
                    createdBy = s.CreatedByUser.FullName,
                    approvedBy = s.ApprovedByUser != null ? s.ApprovedByUser.FullName : null,
                    updatedAt = s.UpdatedAt ?? s.CreatedAt
                })
                .ToListAsync();

            return Ok(new
            {
                count,
                /* Reversed days are left out of the money: they were undone. */
                total = money,
                draftCount = drafts,
                page,
                pageSize,
                pageCount = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize)),
                items = items.Select(i => new
                {
                    i.id, i.sheetNo, i.sheetDate, i.locationId, i.location, i.status,
                    statusName = ExpenseSheetPdf.StatusWord(i.status),
                    i.lines, i.total, i.createdBy, i.approvedBy, i.updatedAt
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the expense sheets");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  WHAT THE GRID NEEDS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The dropdowns for the grid: expense heads, the cash and bank accounts
    /// money can be paid from (each with the payment method it implies), the
    /// methods, the places this person may open a sheet for, and the vendors
    /// already on file so the vendor cell can suggest them.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            var methods = await _db.PaymentMethods.AsNoTracking()
                .Where(m => m.IsActive && m.MethodKind != "credit")
                .OrderBy(m => m.MethodId)
                .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName })
                .ToListAsync();

            var heads = await _db.Accounts.AsNoTracking()
                .Where(a => a.IsActive && !a.IsGroup && a.AccountType.Group.GroupName == "Expenses")
                .OrderBy(a => a.AccountCode)
                .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName })
                .ToListAsync();

            var paidFrom = (await _db.Accounts.AsNoTracking()
                    .Where(a => a.IsActive && !a.IsGroup && a.AccountType.TypeName == "Cash & Bank")
                    .OrderBy(a => a.AccountCode)
                    .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName })
                    .ToListAsync())
                .Select(a => new
                {
                    a.id, a.code, a.name,
                    defaultMethodId = DefaultMethodId(a.name, methods.Select(m => (m.id, m.key)).ToList())
                })
                .ToList();

            var (allowed, defaultLocationId) = await AllowedLocationsAsync();
            var locations = await _db.Locations.AsNoTracking()
                .Where(l => allowed.Contains(l.LocationId))
                .OrderBy(l => l.LocationName)
                .Select(l => new { id = l.LocationId, name = l.LocationName })
                .ToListAsync();

            /* The names typed before, most used first, so "K-Electric" is typed
               once and picked every month after. */
            var vendors = await _db.Expenses.AsNoTracking()
                .GroupBy(e => e.VendorName)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => g.Key)
                .Take(300)
                .ToListAsync();

            return Ok(new
            {
                heads, paidFrom, methods, locations, defaultLocationId, vendors,
                today = Today(),
                canApprove = CanApprove()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the expense sheet lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  OPEN A DAY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The sheet for this date and location: the one already there, or a new
    /// empty draft. "New" on the screen calls this with today's date, so
    /// pressing it twice lands on the same sheet rather than making two.
    /// </summary>
    [HttpPost("open")]
    public async Task<IActionResult> Open([FromBody] SheetOpenRequest? body)
    {
        try
        {
            var date = body?.Date ?? Today();

            /* An expense is money already spent. Yesterday's petty cash is
               entered today all the time, so the past is open; tomorrow is
               not, because nothing has been paid tomorrow yet. */
            if (date > Today())
                return BadRequest(new { message = $"{date:dd MMM yyyy} has not happened yet. Expenses are entered on or after the day the money was paid." });

            var (allowed, defaultLocationId) = await AllowedLocationsAsync();
            var locationId = body?.LocationId is > 0 ? body.LocationId.Value : defaultLocationId;
            if (locationId is null or 0 || !allowed.Contains(locationId.Value))
                return BadRequest(new { message = "Pick a location you work at. You can only open expense sheets for your own places." });

            var existing = await _db.ExpenseSheets.AsNoTracking()
                .Where(s => s.SheetDate == date && s.LocationId == locationId && s.ReversedAt == null)
                .Select(s => new { s.SheetId, s.SheetNo })
                .FirstOrDefaultAsync();
            if (existing is not null)
                return Ok(new { id = existing.SheetId, sheetNo = existing.SheetNo, created = false });

            var period = await _db.FiscalPeriods.AsNoTracking()
                .FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (period is null)
                return BadRequest(new { message = $"No fiscal period covers {date:dd MMM yyyy}, so a sheet for that day could never be approved." });
            if (period.IsClosed)
                return BadRequest(new { message = $"{period.PeriodName} is closed. Nothing more can be entered into it." });

            var draft = await StatusIdAsync(Draft);
            var sheet = new ExpenseSheet
            {
                SheetNo = await NextNumber("EXS"),
                SheetDate = date,
                LocationId = locationId.Value,
                StatusId = draft,
                CreatedByUserId = CurrentUserId(),
                CreatedAt = Now()
            };
            _db.ExpenseSheets.Add(sheet);

            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.GetBaseException().Message.Contains("UX_ExpenseSheet_Day"))
            {
                /* Two people pressed New for the same day in the same instant.
                   The database let one through; hand the other the same sheet.
                   The series number the loser took is simply skipped. */
                _db.ChangeTracker.Clear();
                var winner = await _db.ExpenseSheets.AsNoTracking()
                    .Where(s => s.SheetDate == date && s.LocationId == locationId && s.ReversedAt == null)
                    .Select(s => new { s.SheetId, s.SheetNo })
                    .FirstAsync();
                return Ok(new { id = winner.SheetId, sheetNo = winner.SheetNo, created = false });
            }

            await Log("EXPENSE_SHEET_OPENED", "ExpenseSheet", sheet.SheetNo, $"{date:yyyy-MM-dd} at location {locationId}", 1);
            return Ok(new { id = sheet.SheetId, sheetNo = sheet.SheetNo, created = true });
        }
        catch (Exception ex)
        {
            return Fail(ex, "open the expense sheet");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE DAY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        try
        {
            var s = await _db.ExpenseSheets.AsNoTracking()
                .Where(x => x.SheetId == id)
                .Select(x => new
                {
                    id = x.SheetId,
                    sheetNo = x.SheetNo,
                    sheetDate = x.SheetDate,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    status = x.Status.StatusKey,
                    notes = x.Notes,
                    entryId = x.EntryId,
                    entryNo = x.Entry != null ? x.Entry.EntryNo : null,
                    reversalEntryId = x.Entry != null ? x.Entry.ReversedByEntryId : null,
                    reversalEntryNo = x.Entry != null && x.Entry.ReversedByEntry != null ? x.Entry.ReversedByEntry.EntryNo : null,
                    createdBy = x.CreatedByUser.FullName,
                    createdAt = x.CreatedAt,
                    updatedAt = x.UpdatedAt,
                    approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.FullName : null,
                    approvedAt = x.ApprovedAt,
                    reversedBy = x.ReversedByUser != null ? x.ReversedByUser.FullName : null,
                    reversedAt = x.ReversedAt,
                    reversalReason = x.ReversalReason,
                    lines = x.Lines.OrderBy(l => l.ExpenseId).Select(l => new
                    {
                        id = l.ExpenseId,
                        expenseNo = l.ExpenseNo,
                        expenseAccountId = l.ExpenseAccountId,
                        head = l.ExpenseAccount.AccountName,
                        headCode = l.ExpenseAccount.AccountCode,
                        categoryName = l.CategoryName,
                        description = l.Description,
                        vendorName = l.VendorName,
                        paidFromAccountId = l.PaidFromAccountId,
                        paidFrom = l.PaidFromAccount.AccountName,
                        methodId = l.MethodId,
                        method = l.Method.MethodName,
                        amount = l.Amount,
                        status = l.Status.StatusKey,
                        entryId = l.EntryId,
                        entryNo = l.Entry != null ? l.Entry.EntryNo : null,
                        createdBy = l.CreatedByUser.FullName
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (s is null) return NotFound(new { message = $"No expense sheet with id {id}." });

            var lines = s.lines.Select(l =>
            {
                var excluded = s.status != Reversed && l.status is Reversed or "REJECTED" or "CANCELLED";
                /* A line can be typed over only while it is a draft that has
                   never reached the ledger. Anything else is history. */
                var locked = l.status != Draft || l.entryId is not null;
                return new
                {
                    l.id, l.expenseNo, l.expenseAccountId, l.head, l.headCode, l.categoryName,
                    l.description, l.vendorName, l.paidFromAccountId, l.paidFrom, l.methodId, l.method,
                    l.amount, l.status, l.entryId, l.entryNo, l.createdBy,
                    locked, excluded
                };
            }).ToList();

            var live = lines.Where(l => !l.excluded).ToList();

            /* A sheet made by migration 35 out of expenses that were each
               posted on their own has one entry per line and none of its own. */
            var entries = lines.Where(l => l.entryId is not null && l.entryId != s.entryId)
                .Select(l => new { id = l.entryId!.Value, no = l.entryNo, reversed = l.status == Reversed })
                .DistinctBy(e => e.id).ToList();

            /* The corrected day, once a reversed one has been re-entered. */
            int? liveSheetId = null;
            if (s.status == Reversed)
                liveSheetId = await _db.ExpenseSheets.AsNoTracking()
                    .Where(x => x.SheetDate == s.sheetDate && x.LocationId == s.locationId && x.ReversedAt == null)
                    .Select(x => (int?)x.SheetId).FirstOrDefaultAsync();

            var (allowed, _) = await AllowedLocationsAsync();
            var hasDrafts = lines.Any(l => !l.locked);

            return Ok(new
            {
                s.id, s.sheetNo, s.sheetDate, s.locationId, s.location, s.status,
                statusName = ExpenseSheetPdf.StatusWord(s.status),
                s.notes, s.entryId, s.entryNo, s.reversalEntryId, s.reversalEntryNo,
                perLineEntries = entries,
                s.createdBy, s.createdAt, s.updatedAt, s.approvedBy, s.approvedAt,
                s.reversedBy, s.reversedAt, s.reversalReason,
                lines,
                total = live.Sum(l => l.amount),
                lineCount = live.Count,
                byPaidFrom = live.GroupBy(l => new { l.paidFromAccountId, l.paidFrom })
                    .Select(g => new { accountId = g.Key.paidFromAccountId, account = g.Key.paidFrom, count = g.Count(), amount = g.Sum(x => x.amount) })
                    .OrderByDescending(g => g.amount).ToList(),
                liveSheetId,
                canEdit = s.status == Draft && allowed.Contains(s.locationId),
                canApprove = s.status == Draft && hasDrafts && CanApprove(),
                canReverse = s.status == Posted && CanApprove(),
                canDelete = s.status == Draft && lines.All(l => !l.locked),
                canReenter = s.status == Reversed && liveSheetId is null && allowed.Contains(s.locationId)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load expense sheet {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SAVE THE WHOLE DAY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Replaces the sheet's editable lines with the ones sent, in one go: a
    /// line with an id is updated, a line without one is added, and an
    /// editable line that is no longer sent is deleted. Lines that already
    /// reached the ledger on their own (pre-sheet expenses) are never touched,
    /// whether they are sent or not.
    ///
    /// Every line is checked before anything is written, and the refusal says
    /// which row and which cell, so the grid can put the message under the
    /// cell rather than in a toast nobody can match to a row.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Save(int id, [FromBody] SheetSaveRequest body)
    {
        try
        {
            var sheet = await _db.ExpenseSheets
                .Include(s => s.Status)
                .Include(s => s.Lines).ThenInclude(l => l.Status)
                .FirstOrDefaultAsync(s => s.SheetId == id);
            if (sheet is null) return NotFound(new { message = $"No expense sheet with id {id}." });

            if (sheet.Status.StatusKey != Draft)
                return BadRequest(new
                {
                    message = sheet.Status.StatusKey == Posted
                        ? $"{sheet.SheetNo} is approved and in the ledger. Reverse it to correct the day."
                        : $"{sheet.SheetNo} is {ExpenseSheetPdf.StatusWord(sheet.Status.StatusKey).ToLowerInvariant()} and can no longer be changed."
                });

            var (allowed, _) = await AllowedLocationsAsync();
            if (!allowed.Contains(sheet.LocationId))
                return StatusCode(403, new { message = "This sheet belongs to a location you do not work at." });

            var incoming = body?.Lines ?? new List<SheetLineRequest>();
            if (incoming.Count > MaxLines)
                return BadRequest(new { message = $"A day sheet holds at most {MaxLines} expenses." });
            if (body?.Notes is { Length: > 500 })
                return BadRequest(new { message = "The note is longer than 500 characters." });

            var heads = await _db.Accounts.AsNoTracking()
                .Where(a => !a.IsGroup && a.AccountType.Group.GroupName == "Expenses")
                .ToDictionaryAsync(a => a.AccountId, a => a.AccountName);
            var paidFrom = await _db.Accounts.AsNoTracking()
                .Where(a => !a.IsGroup && a.AccountType.TypeName == "Cash & Bank")
                .ToDictionaryAsync(a => a.AccountId, a => a.AccountName);
            var methods = await _db.PaymentMethods.AsNoTracking()
                .Where(m => m.IsActive)
                .Select(m => new { m.MethodId, m.MethodKey })
                .ToListAsync();
            var methodPairs = methods.Select(m => (m.MethodId, m.MethodKey)).ToList();

            var byId = sheet.Lines.ToDictionary(l => l.ExpenseId);
            var errors = new List<LineError>();

            for (var i = 0; i < incoming.Count; i++)
            {
                var r = incoming[i];
                var row = i;
                void Bad(string field, string message) => errors.Add(new LineError(row, field, message));

                if (r.Id is > 0 && !byId.ContainsKey(r.Id.Value))
                    Bad("row", "This line is not on this sheet any more. Reload the sheet.");
                if (!heads.ContainsKey(r.ExpenseAccountId))
                    Bad("expenseAccountId", "Pick an expense head.");
                if (!paidFrom.ContainsKey(r.PaidFromAccountId))
                    Bad("paidFromAccountId", "Pick the cash or bank account it was paid from.");
                if (string.IsNullOrWhiteSpace(r.VendorName))
                    Bad("vendorName", "Who was paid?");
                else if (r.VendorName.Trim().Length > 150)
                    Bad("vendorName", "Keep the vendor under 150 characters.");
                if (r.Description is { Length: > 500 })
                    Bad("description", "Keep the description under 500 characters.");
                if (r.Amount <= 0)
                    Bad("amount", "Enter an amount above zero.");
                else if (r.Amount >= 1_000_000_000_000m)
                    Bad("amount", "That amount is too large.");
                if (r.MethodId is > 0 && methods.All(m => m.MethodId != r.MethodId))
                    Bad("methodId", "Pick a valid payment method.");
            }

            if (errors.Count > 0)
            {
                var rowsWithErrors = errors.Select(e => e.Row).Distinct().Count();
                return BadRequest(new
                {
                    message = rowsWithErrors == 1
                        ? "One line needs attention before the sheet can be saved."
                        : $"{rowsWithErrors} lines need attention before the sheet can be saved.",
                    errors
                });
            }

            var hadLines = sheet.Lines.Any(l => l.Status.StatusKey != Reversed);
            var draftId = await StatusIdAsync(Draft);

            /* New line numbers carry on from the highest one this sheet has
               ever used, so a number is never handed out twice -- not even to
               a line deleted a minute ago, whose PDF might already exist. */
            var prefix = sheet.SheetNo + "-";
            var next = sheet.Lines
                .Where(l => l.ExpenseNo.StartsWith(prefix))
                .Select(l => int.TryParse(l.ExpenseNo[prefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0).Max() + 1;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var keep = new HashSet<int>();
            foreach (var r in incoming)
            {
                Expense line;
                if (r.Id is > 0)
                {
                    line = byId[r.Id.Value];
                    keep.Add(line.ExpenseId);
                    /* Already in the ledger on its own: history, left alone. */
                    if (IsLocked(line)) continue;
                }
                else
                {
                    line = new Expense
                    {
                        ExpenseNo = $"{prefix}{next++:D2}",
                        SheetId = sheet.SheetId,
                        StatusId = draftId,
                        CreatedByUserId = CurrentUserId()
                    };
                    _db.Expenses.Add(line);
                }

                var head = heads[r.ExpenseAccountId];
                line.ExpenseDate = sheet.SheetDate;
                line.LocationId = sheet.LocationId;
                line.ExpenseAccountId = r.ExpenseAccountId;
                /* The category every expense report groups by is the head's
                   own name now, rather than a second word typed by hand that
                   drifted from it ("Utilities (KE)" booked to Rent). */
                line.CategoryName = head.Length > 80 ? head[..80] : head;
                line.PaidFromAccountId = r.PaidFromAccountId;
                line.MethodId = r.MethodId is > 0
                    ? r.MethodId.Value
                    : DefaultMethodId(paidFrom[r.PaidFromAccountId], methodPairs);
                line.Amount = Math.Round(r.Amount, 2, MidpointRounding.AwayFromZero);
                line.VendorName = r.VendorName!.Trim();
                line.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
            }

            /* The editable lines the grid no longer holds are the ones the
               user deleted. */
            var gone = sheet.Lines.Where(l => !keep.Contains(l.ExpenseId) && l.ExpenseId > 0 && !IsLocked(l)).ToList();
            _db.Expenses.RemoveRange(gone);

            sheet.Notes = string.IsNullOrWhiteSpace(body?.Notes) ? null : body.Notes.Trim();
            sheet.UpdatedAt = Now();

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var liveLines = await _db.Expenses.AsNoTracking()
                .Where(l => l.SheetId == id && l.Status.StatusKey != Reversed
                            && l.Status.StatusKey != "REJECTED" && l.Status.StatusKey != "CANCELLED")
                .Select(l => l.Amount).ToListAsync();

            await Log("EXPENSE_SHEET_SAVED", "ExpenseSheet", sheet.SheetNo,
                $"{liveLines.Count} lines, {liveLines.Sum():N2}" + (gone.Count > 0 ? $", {gone.Count} removed" : ""), 1);

            /* The stored PDF follows the sheet, so Print always prints what is
               on screen. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, DocKind, id, CurrentUserId());

            /* Told once, when the day first has something on it -- not on every
               save of a sheet typed through the afternoon. */
            if (!hadLines && liveLines.Count > 0)
                await _push.NotifyRolesAsync(
                    new[] { "super-admin", "accountant" },
                    NotificationKinds.ExpenseCreated,
                    $"Expense sheet started by {CurrentUserName()}",
                    $"{sheet.SheetNo} -- {sheet.SheetDate:dd MMM}: {liveLines.Count} expense(s), PKR {liveLines.Sum():N0}. Waiting for approval.",
                    url: $"/accounting/expenses/sheets/{id}",
                    exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                sheetNo = sheet.SheetNo,
                lineCount = liveLines.Count,
                total = liveLines.Sum(),
                message = $"{sheet.SheetNo} saved -- {liveLines.Count} {(liveLines.Count == 1 ? "expense" : "expenses")}, PKR {liveLines.Sum():N0}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"save expense sheet {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  APPROVE THE DAY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Approves the whole day in one click and posts it: ONE journal entry,
    /// a debit per expense to its head and a credit per paid-from account.
    ///
    /// The old per-expense approval wrote one two-line entry per expense; a
    /// day of twelve expenses was twelve entries. This is one entry that
    /// reads, in the ledger, exactly like the printed sheet: the lines, then
    /// what each drawer or bank account paid out. Each expense row gets the
    /// entry's id, so the per-expense history screen and every report that
    /// reads "Expense" stay right.
    /// </summary>
    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> Approve(int id)
    {
        try
        {
            var sheet = await _db.ExpenseSheets
                .Include(s => s.Status)
                .Include(s => s.Location)
                .Include(s => s.Lines).ThenInclude(l => l.Status)
                .FirstOrDefaultAsync(s => s.SheetId == id);
            if (sheet is null) return NotFound(new { message = $"No expense sheet with id {id}." });
            if (sheet.Status.StatusKey != Draft)
                return BadRequest(new { message = $"{sheet.SheetNo} is already {ExpenseSheetPdf.StatusWord(sheet.Status.StatusKey).ToLowerInvariant()}." });

            var toPost = sheet.Lines.Where(l => !IsLocked(l)).OrderBy(l => l.ExpenseId).ToList();
            if (toPost.Count == 0)
                return BadRequest(new { message = $"{sheet.SheetNo} has no expenses to approve. Enter the day's expenses and save first." });

            /* Checked again here, not only on save: migration 35 filed old
               drafts that were typed before the head had to be an expense
               account -- EXP-26-0026 on 30 Aug is booked to Owner Capital --
               and approving that as it stands would debit the owner's equity
               and call it an expense. */
            var postIds = toPost.SelectMany(l => new[] { l.ExpenseAccountId, l.PaidFromAccountId }).Distinct().ToList();
            /* Projected first: ToDictionaryAsync's selectors run in memory,
               where AccountType was never loaded. */
            var kinds = (await _db.Accounts.AsNoTracking()
                    .Where(a => postIds.Contains(a.AccountId))
                    .Select(a => new
                    {
                        a.AccountId, a.IsGroup,
                        group = a.AccountType.Group.GroupName, type = a.AccountType.TypeName
                    })
                    .ToListAsync())
                .ToDictionary(a => a.AccountId);
            var wrong = toPost.Select((l, i) => (l, i))
                .Where(x => !kinds.TryGetValue(x.l.ExpenseAccountId, out var h) || h.IsGroup || h.group != "Expenses"
                         || !kinds.TryGetValue(x.l.PaidFromAccountId, out var p) || p.IsGroup || p.type != "Cash & Bank")
                .Select(x => x.l.ExpenseNo)
                .ToList();
            if (wrong.Count > 0)
                return BadRequest(new
                {
                    message = $"{string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not booked to an expense head paid from a cash or bank account. Pick the right head on the sheet, save, then approve."
                });

            var period = await _db.FiscalPeriods
                .FirstOrDefaultAsync(p => p.StartDate <= sheet.SheetDate && p.EndDate >= sheet.SheetDate);
            if (period is null) return BadRequest(new { message = $"No fiscal period covers {sheet.SheetDate:dd MMM yyyy}, so {sheet.SheetNo} cannot be posted." });
            if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. {sheet.SheetNo} cannot be posted into it." });

            var postedId = await StatusIdAsync(Posted);
            var type = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "EXPENSE")
                       ?? await _db.JournalEntryTypes.FirstAsync();
            var accounts = await _db.Accounts.AsNoTracking()
                .Where(a => toPost.Select(l => l.PaidFromAccountId).Contains(a.AccountId))
                .ToDictionaryAsync(a => a.AccountId, a => new { a.AccountCode, a.AccountName });

            var total = toPost.Sum(l => l.Amount);

            await using var tx = await _db.Database.BeginTransactionAsync();

            var entry = new JournalEntry
            {
                EntryNo = await NextNumber("JV"),
                EntryDate = sheet.SheetDate,
                EntryTypeId = type.EntryTypeId,
                PeriodId = period.PeriodId,
                LocationId = sheet.LocationId,
                ReferenceNo = sheet.SheetNo,
                Narration = Cut($"Daily expenses {sheet.SheetDate:dd MMM yyyy} -- {sheet.Location.LocationName} " +
                                $"({toPost.Count} {(toPost.Count == 1 ? "expense" : "expenses")}, {sheet.SheetNo})", 500),
                StatusId = postedId,
                CreatedByUserId = CurrentUserId(),
                PostedByUserId = CurrentUserId(),
                CreatedAt = Today()
            };
            _db.JournalEntries.Add(entry);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in toPost)
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = n++, AccountId = l.ExpenseAccountId,
                    Description = Cut(string.IsNullOrWhiteSpace(l.Description)
                        ? $"{l.VendorName} ({l.ExpenseNo})"
                        : $"{l.VendorName} -- {l.Description} ({l.ExpenseNo})", 300),
                    DebitAmount = l.Amount, CreditAmount = 0m
                });

            /* One credit per drawer or bank account, in code order: the cash
               book shows one payment-out for the day per account, which is what
               the cashier counts the drawer against. */
            foreach (var g in toPost.GroupBy(l => l.PaidFromAccountId)
                                    .OrderBy(g => accounts.TryGetValue(g.Key, out var a) ? a.AccountCode : ""))
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = n++, AccountId = g.Key,
                    Description = Cut($"Paid out on {sheet.SheetNo} -- {g.Count()} {(g.Count() == 1 ? "expense" : "expenses")}", 300),
                    DebitAmount = 0m, CreditAmount = g.Sum(l => l.Amount)
                });

            foreach (var l in toPost)
            {
                l.EntryId = entry.EntryId;
                l.StatusId = postedId;
            }

            sheet.EntryId = entry.EntryId;
            sheet.StatusId = postedId;
            sheet.ApprovedByUserId = CurrentUserId();
            sheet.ApprovedAt = Now();

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("EXPENSE_SHEET_APPROVED", "ExpenseSheet", sheet.SheetNo,
                $"{toPost.Count} lines, {total:N2}, posted as {entry.EntryNo}", 2);
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, DocKind, id, CurrentUserId());

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.ExpenseDecided,
                $"Expense sheet approved by {CurrentUserName()}",
                $"{sheet.SheetNo} -- {sheet.SheetDate:dd MMM}, {sheet.Location.LocationName}: PKR {total:N0} posted as {entry.EntryNo}.",
                url: $"/accounting/expenses/sheets/{id}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: toPost.Select(l => l.CreatedByUserId).Append(sheet.CreatedByUserId).Distinct());

            return Ok(new
            {
                id,
                status = Posted,
                entryId = entry.EntryId,
                entryNo = entry.EntryNo,
                message = $"{sheet.SheetNo} approved -- PKR {total:N0} posted as {entry.EntryNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"approve expense sheet {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  REVERSE THE DAY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Undoes an approved day, on the pattern every other reversal here
    /// follows: nothing is edited or deleted, a mirror entry is written, the
    /// original entry keeps POSTED and points at its mirror, and the sheet and
    /// its lines become REVERSED. The date can then be opened again and the
    /// corrected day entered (or copied across with Re-enter).
    ///
    /// The mirror is dated on the sheet's own day when that month is still
    /// open, so the day's books net to nothing and the corrected sheet lands
    /// in the same place; into a closed month it goes on today instead.
    /// </summary>
    [HttpPost("{id:int}/reverse")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> Reverse(int id, [FromBody] SheetReverseRequest? body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Reason))
                return BadRequest(new { message = "Reversing a day needs a reason." });
            var reason = Cut(body!.Reason!.Trim(), 450);

            var sheet = await _db.ExpenseSheets
                .Include(s => s.Status)
                .Include(s => s.Location)
                .Include(s => s.Lines).ThenInclude(l => l.Status)
                .FirstOrDefaultAsync(s => s.SheetId == id);
            if (sheet is null) return NotFound(new { message = $"No expense sheet with id {id}." });
            if (sheet.Status.StatusKey != Posted)
                return BadRequest(new { message = $"Only an approved sheet can be reversed. {sheet.SheetNo} is {ExpenseSheetPdf.StatusWord(sheet.Status.StatusKey).ToLowerInvariant()}." });

            var postedLines = sheet.Lines.Where(l => l.Status.StatusKey == Posted).ToList();

            /* Every entry behind this day that still stands: the sheet's own,
               or -- for a day migration 35 made from expenses posted one at a
               time -- each line's. One already reversed on its own (through
               Journal Entries) is left alone: it is already undone. */
            var entryIds = postedLines.Where(l => l.EntryId != null).Select(l => l.EntryId!.Value)
                .Append(sheet.EntryId ?? 0).Where(e => e > 0).Distinct().ToList();
            var originals = await _db.JournalEntries
                .Include(e => e.JournalEntryLines)
                .Where(e => entryIds.Contains(e.EntryId) && e.ReversedByEntryId == null)
                .OrderBy(e => e.EntryId)
                .ToListAsync();

            DateOnly date;
            FiscalPeriod? period = null;
            if (body.ReverseDate is { } asked)
            {
                date = asked;
            }
            else
            {
                period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.StartDate <= sheet.SheetDate && p.EndDate >= sheet.SheetDate);
                date = period is { IsClosed: false } ? sheet.SheetDate : Today();
                if (date != sheet.SheetDate) period = null;
            }
            period ??= await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (originals.Count > 0)
            {
                if (period is null) return BadRequest(new { message = $"No fiscal period covers {date:dd MMM yyyy}." });
                if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. Pick another reversal date." });
            }

            var reversedId = await StatusIdAsync(Reversed);
            var postedId = await StatusIdAsync(Posted);

            await using var tx = await _db.Database.BeginTransactionAsync();

            string? mirrorNo = null;
            if (originals.Count > 0)
            {
                var first = originals[0];
                var mirror = new JournalEntry
                {
                    EntryNo = await NextNumber("JV"),
                    EntryDate = date,
                    EntryTypeId = first.EntryTypeId,
                    PeriodId = period!.PeriodId,
                    LocationId = sheet.LocationId,
                    ReferenceNo = originals.Count == 1 ? first.EntryNo : sheet.SheetNo,
                    Narration = Cut($"Reversal of {string.Join(", ", originals.Select(o => o.EntryNo))} ({sheet.SheetNo}) -- {reason}", 500),
                    /* POSTED, like every mirror: both sides count and cancel. */
                    StatusId = postedId,
                    CreatedByUserId = CurrentUserId(),
                    PostedByUserId = CurrentUserId(),
                    CreatedAt = Today()
                };
                _db.JournalEntries.Add(mirror);
                await _db.SaveChangesAsync();

                short n = 1;
                foreach (var o in originals)
                    foreach (var l in o.JournalEntryLines.OrderBy(l => l.LineNo))
                        _db.JournalEntryLines.Add(new JournalEntryLine
                        {
                            EntryId = mirror.EntryId, LineNo = n++, AccountId = l.AccountId,
                            PartyUserId = l.PartyUserId,
                            Description = Cut($"Reversal: {l.Description}", 300),
                            DebitAmount = l.CreditAmount,      // the swap IS the reversal
                            CreditAmount = l.DebitAmount
                        });

                /* Same rule as ReverseJournalEntry and ReverseExpense: the
                   originals keep POSTED so the pair cancels in every statement;
                   this link is what tells the screens it was undone. */
                foreach (var o in originals) o.ReversedByEntryId = mirror.EntryId;
                mirrorNo = mirror.EntryNo;
            }

            foreach (var l in postedLines) l.StatusId = reversedId;
            sheet.StatusId = reversedId;
            sheet.ReversedByUserId = CurrentUserId();
            sheet.ReversedAt = Now();
            sheet.ReversalReason = reason;

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var total = postedLines.Sum(l => l.Amount);
            await Log("EXPENSE_SHEET_REVERSED", "ExpenseSheet", sheet.SheetNo,
                $"{reason}" + (mirrorNo is null ? "" : $" -- by {mirrorNo}"), 3);
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, DocKind, id, CurrentUserId());

            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.ExpenseReversed,
                $"Expense sheet reversed by {CurrentUserName()}",
                $"{sheet.SheetNo} -- {sheet.SheetDate:dd MMM}, PKR {total:N0} undone" +
                (mirrorNo is null ? "." : $" by {mirrorNo}.") + $" Reason: {reason}",
                url: $"/accounting/expenses/sheets/{id}",
                severe: true,
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                status = Reversed,
                reversalEntryNo = mirrorNo,
                message = mirrorNo is null
                    ? $"{sheet.SheetNo} reversed."
                    : $"{sheet.SheetNo} reversed by {mirrorNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reverse expense sheet {id}");
        }
    }

    /// <summary>
    /// After a day has been reversed: open a fresh draft for the same date and
    /// location holding copies of the reversed lines, so the correction is an
    /// edit of what was there rather than the whole day typed again.
    /// </summary>
    [HttpPost("{id:int}/reenter")]
    public async Task<IActionResult> Reenter(int id)
    {
        try
        {
            var old = await _db.ExpenseSheets.AsNoTracking()
                .Include(s => s.Status)
                .Include(s => s.Lines).ThenInclude(l => l.Status)
                .FirstOrDefaultAsync(s => s.SheetId == id);
            if (old is null) return NotFound(new { message = $"No expense sheet with id {id}." });
            if (old.Status.StatusKey != Reversed)
                return BadRequest(new { message = $"Only a reversed sheet can be re-entered. {old.SheetNo} is {ExpenseSheetPdf.StatusWord(old.Status.StatusKey).ToLowerInvariant()}." });

            var (allowed, _) = await AllowedLocationsAsync();
            if (!allowed.Contains(old.LocationId))
                return StatusCode(403, new { message = "This sheet belongs to a location you do not work at." });

            var standing = await _db.ExpenseSheets.AsNoTracking()
                .Where(s => s.SheetDate == old.SheetDate && s.LocationId == old.LocationId && s.ReversedAt == null)
                .Select(s => new { s.SheetId, s.SheetNo }).FirstOrDefaultAsync();
            if (standing is not null)
                return BadRequest(new { message = $"{standing.SheetNo} is already open for that day.", id = standing.SheetId });

            var period = await _db.FiscalPeriods.AsNoTracking()
                .FirstOrDefaultAsync(p => p.StartDate <= old.SheetDate && p.EndDate >= old.SheetDate);
            if (period is null || period.IsClosed)
                return BadRequest(new { message = $"{period?.PeriodName ?? "That month"} is closed. The day cannot be entered again." });

            var draftId = await StatusIdAsync(Draft);

            await using var tx = await _db.Database.BeginTransactionAsync();
            var sheet = new ExpenseSheet
            {
                SheetNo = await NextNumber("EXS"),
                SheetDate = old.SheetDate,
                LocationId = old.LocationId,
                StatusId = draftId,
                Notes = Cut($"Re-entered from {old.SheetNo}, which was reversed: {old.ReversalReason}", 500),
                CreatedByUserId = CurrentUserId(),
                CreatedAt = Now()
            };
            _db.ExpenseSheets.Add(sheet);
            await _db.SaveChangesAsync();

            var k = 1;
            foreach (var l in old.Lines.Where(l => l.Status.StatusKey == Reversed).OrderBy(l => l.ExpenseId))
                _db.Expenses.Add(new Expense
                {
                    ExpenseNo = $"{sheet.SheetNo}-{k++:D2}",
                    SheetId = sheet.SheetId,
                    ExpenseDate = sheet.SheetDate,
                    LocationId = sheet.LocationId,
                    CategoryName = l.CategoryName,
                    ExpenseAccountId = l.ExpenseAccountId,
                    PaidFromAccountId = l.PaidFromAccountId,
                    Amount = l.Amount,
                    VendorName = l.VendorName,
                    MethodId = l.MethodId,
                    Description = l.Description,
                    StatusId = draftId,
                    CreatedByUserId = CurrentUserId()
                });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("EXPENSE_SHEET_REENTERED", "ExpenseSheet", sheet.SheetNo, $"from {old.SheetNo}", 1);
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, DocKind, sheet.SheetId, CurrentUserId());

            return Ok(new { id = sheet.SheetId, sheetNo = sheet.SheetNo, message = $"{sheet.SheetNo} opened with {k - 1} lines copied from {old.SheetNo}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"re-enter expense sheet {id}");
        }
    }

    /// <summary>
    /// Throws away a draft day -- opened for the wrong date, say. Refused when
    /// any line on it already reached the ledger on its own.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var sheet = await _db.ExpenseSheets
                .Include(s => s.Status)
                .Include(s => s.Lines).ThenInclude(l => l.Status)
                .FirstOrDefaultAsync(s => s.SheetId == id);
            if (sheet is null) return NotFound(new { message = $"No expense sheet with id {id}." });
            if (sheet.Status.StatusKey != Draft)
                return BadRequest(new { message = $"{sheet.SheetNo} is {ExpenseSheetPdf.StatusWord(sheet.Status.StatusKey).ToLowerInvariant()}. Only a draft can be deleted." });
            if (sheet.Lines.Any(IsLocked))
                return BadRequest(new { message = $"{sheet.SheetNo} holds expenses that were already posted or reversed on their own. It stays as their record." });

            var (allowed, _) = await AllowedLocationsAsync();
            if (!allowed.Contains(sheet.LocationId))
                return StatusCode(403, new { message = "This sheet belongs to a location you do not work at." });

            var no = sheet.SheetNo;
            var count = sheet.Lines.Count;
            var total = sheet.Lines.Sum(l => l.Amount);

            await using var tx = await _db.Database.BeginTransactionAsync();
            _db.Expenses.RemoveRange(sheet.Lines);
            _db.ExpenseSheets.Remove(sheet);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("EXPENSE_SHEET_DELETED", "ExpenseSheet", no, $"draft with {count} lines, {total:N2}", 3);
            return Ok(new { id, message = $"{no} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete expense sheet {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  HELPERS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A line that may no longer be typed over: anything but a draft, and any
    /// draft that somehow already carries an entry.
    /// </summary>
    private static bool IsLocked(Expense l) => l.Status.StatusKey != Draft || l.EntryId is not null;

    private bool CanApprove() => CurrentRole() is "accountant" or "super-admin";

    private async Task<int> StatusIdAsync(string key) =>
        await _db.PostingStatuses.Where(s => s.StatusKey == key).Select(s => s.StatusId).FirstAsync();

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>
    /// The places this person may open a sheet for, and the one to offer
    /// first.
    ///
    /// The owner's rule: "default to the user's location, which they can
    /// change if they have more than one." A Super Admin may open any; anybody
    /// else the places they WORK at (User.LocationsNavigation -- the
    /// UserLocation junction, trap 4) plus their primary one. Claim Stock is
    /// left out: it is a shelf for damaged goods, not a place with a cash
    /// drawer. Somebody with no place on file at all gets every place rather
    /// than a screen they cannot use, the same fallback the rest of the app
    /// takes.
    /// </summary>
    private async Task<(List<int> Allowed, int? Default)> AllowedLocationsAsync()
    {
        var active = await _db.Locations.AsNoTracking()
            .Where(l => l.IsActive && l.Kind.KindKey != "claim")
            .OrderBy(l => l.LocationName)
            .Select(l => l.LocationId)
            .ToListAsync();

        var uid = CurrentUserId();
        var me = await _db.Users.AsNoTracking()
            .Where(u => u.UserId == uid)
            .Select(u => new { u.PrimaryLocationId, places = u.LocationsNavigation.Select(l => l.LocationId).ToList() })
            .FirstOrDefaultAsync();

        List<int> allowed;
        if (CurrentRole() == "super-admin" || me is null)
        {
            allowed = active;
        }
        else
        {
            var mine = me.places.ToList();
            if (me.PrimaryLocationId is { } p) mine.Add(p);
            allowed = active.Where(mine.Contains).ToList();
            if (allowed.Count == 0) allowed = active;
        }

        int? preferred = me?.PrimaryLocationId is { } primary && allowed.Contains(primary)
            ? primary
            : allowed.Cast<int?>().FirstOrDefault();
        return (allowed, preferred);
    }

    /// <summary>
    /// The payment method a paid-from account implies, so nobody has to pick
    /// "Cash" after picking "Cash on Hand". Read from the account's name
    /// because the chart of accounts has no column linking the two; the grid
    /// lets it be changed when the guess is wrong (a bank account paid by
    /// cheque).
    /// </summary>
    private static int DefaultMethodId(string accountName, List<(int Id, string Key)> methods)
    {
        var n = accountName.ToLowerInvariant();
        /* JazzCash before cash: "JazzCash Wallet" contains "cash". */
        var key = n.Contains("jazz") ? "JAZZCASH"
                : n.Contains("easypaisa") ? "EASYPAISA"
                : n.Contains("petty") ? "PETTY_CASH"
                : n.Contains("cash") ? "CASH"
                : "BANK";
        return methods.FirstOrDefault(m => m.Key == key).Id is var hit and > 0
            ? hit
            : methods.FirstOrDefault().Id;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record SheetOpenRequest(DateOnly? Date, int? LocationId);

    public record SheetLineRequest(
        int? Id, int ExpenseAccountId, string? Description, string? VendorName,
        int PaidFromAccountId, int? MethodId, decimal Amount);

    public record SheetSaveRequest(string? Notes, List<SheetLineRequest>? Lines);

    public record SheetReverseRequest(string? Reason, DateOnly? ReverseDate);

    /// <summary>One refused cell: which row of the request, which field, and why.</summary>
    public record LineError(int Row, string Field, string Message);
}
