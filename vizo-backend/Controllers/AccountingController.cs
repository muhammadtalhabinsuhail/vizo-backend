using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /accounting screens: chart of accounts, ledgers, journal entries,
/// vouchers, expenses, collections, period close, reconciliation and the four
/// financial statements.
///
/// ONE RULE RUNS THROUGH ALL OF IT: only a POSTED entry counts. Every balance,
/// every statement and every ledger filters on PostingStatus.StatusKey ==
/// "POSTED". A draft entry is a piece of paper somebody is still typing, and
/// including it is how a set of books stops agreeing with itself.
///
/// The second rule is the collections control gap the business asked for: cash
/// a rep collects in the field sits at AWAITING until the accountant confirms
/// it, and does NOT move the customer ledger before then. Do not "fix" this.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/accounting")]
[ApiController]
[Authorize(Policy = "Accountant")]
public class AccountingController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public AccountingController(AppDbContext db, IConfiguration cfg,
        ILogger<AccountingController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    private const string Posted = "POSTED";

    /* The real "AccountGroup".GroupName values on this database. They are
       plural and they are not the words you would guess -- "Capital" rather
       than Equity, "Revenue" rather than Income. Getting one wrong does not
       error, it silently returns an empty statement, which is how a profit and
       loss ends up reading 0 / 0. Named here so there is one place to be
       right. */
    private const string GroupAssets = "Assets";
    private const string GroupCapital = "Capital";
    private const string GroupExpenses = "Expenses";
    private const string GroupLiabilities = "Liabilities";
    private const string GroupRevenue = "Revenue";

    /* "Account".OpeningBalance is stored as a POSITIVE magnitude in the
       account's own natural sense: a Sale account opens at +21,800,000 even
       though a sale is a credit. Ledger arithmetic runs on a debit basis
       (debit - credit), so a credit-normal opening balance has to be flipped
       before it can be added to movement, or every credit-normal account lands
       on the debit side and the trial balance does not balance. */
    private static decimal ToDebitBasis(decimal opening, bool isDebitNormal) =>
        isDebitNormal ? opening : -opening;

    // ══════════════════════════════════════════════════════════════════
    //  CHART OF ACCOUNTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("coa")]
    public async Task<IActionResult> GetChartOfAccounts([FromQuery] bool includeInactive = true)
    {
        try
        {
            var rows = _db.Accounts.AsNoTracking().AsQueryable();
            if (!includeInactive) rows = rows.Where(a => a.IsActive);

            var accounts = await rows
                .OrderBy(a => a.AccountCode)
                .Select(a => new
                {
                    id = a.AccountId,
                    code = a.AccountCode,
                    name = a.AccountName,
                    parentId = a.ParentAccountId,
                    accountTypeId = a.AccountTypeId,
                    type = a.AccountType.TypeName,
                    group = a.AccountType.Group.GroupName,
                    isDebitNormal = a.AccountType.IsDebitNormal,
                    isGroup = a.IsGroup,
                    openingBalance = a.OpeningBalance,
                    currency = a.CurrencyCode,
                    isActive = a.IsActive,
                    movement = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == Posted)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m
                })
                .ToListAsync();

            /* A debit-normal account (asset, expense) is positive when debits
               exceed credits; a credit-normal one (liability, equity, income)
               is the other way round. Signing it here means the screen can just
               print the number. */
            return Ok(accounts.Select(a => new
            {
                a.id, a.code, a.name, a.parentId, a.accountTypeId, a.type, a.group,
                a.isGroup, a.openingBalance, a.currency, a.isActive,
                balance = a.isDebitNormal
                    ? a.openingBalance + a.movement
                    : a.openingBalance - a.movement
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the chart of accounts");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LEDGER
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("ledger")]
    public async Task<IActionResult> GetLedger(
        [FromQuery] int accountId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var account = await _db.Accounts.AsNoTracking()
                .Where(a => a.AccountId == accountId)
                .Select(a => new
                {
                    a.AccountId, a.AccountCode, a.AccountName, a.OpeningBalance,
                    Type = a.AccountType.TypeName,
                    a.AccountType.IsDebitNormal
                })
                .FirstOrDefaultAsync();

            if (account is null) return NotFound(new { message = $"No account with id {accountId}." });

            var q = _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.AccountId == accountId && l.Entry.Status.StatusKey == Posted);

            /* Anything before `from` is folded into the opening figure so the
               running balance is still true when the page is date-filtered. */
            var brought = ToDebitBasis(account.OpeningBalance, account.IsDebitNormal);
            if (from is not null)
            {
                brought += await q.Where(l => l.Entry.EntryDate < from)
                    .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
                q = q.Where(l => l.Entry.EntryDate >= from);
            }
            if (to is not null) q = q.Where(l => l.Entry.EntryDate <= to);

            var lines = await q
                .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.LineId)
                .Select(l => new
                {
                    id = l.LineId,
                    date = l.Entry.EntryDate,
                    entryNo = l.Entry.EntryNo,
                    entryType = l.Entry.EntryType.TypeName,
                    reference = l.Entry.ReferenceNo,
                    narration = l.Description ?? l.Entry.Narration,
                    party = l.PartyUser != null ? l.PartyUser.LegalName : null,
                    debit = l.DebitAmount,
                    credit = l.CreditAmount
                })
                .ToListAsync();

            var running = brought;
            var rows = lines.Select(l =>
            {
                running += l.debit - l.credit;
                return new
                {
                    l.id, l.date, l.entryNo, l.entryType, l.reference, l.narration,
                    l.party, l.debit, l.credit, balance = running
                };
            }).ToList();

            return Ok(new
            {
                account = new
                {
                    id = account.AccountId,
                    code = account.AccountCode,
                    name = account.AccountName,
                    type = account.Type,
                    isDebitNormal = account.IsDebitNormal
                },
                openingBalance = brought,
                closingBalance = running,
                totalDebit = rows.Sum(r => r.debit),
                totalCredit = rows.Sum(r => r.credit),
                lines = rows
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the ledger for account {accountId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  JOURNAL ENTRIES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("journal-entries")]
    public async Task<IActionResult> GetJournalEntries(
        [FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.JournalEntries.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(e => e.Status.StatusKey == status);
            if (from is not null) rows = rows.Where(e => e.EntryDate >= from);
            if (to is not null) rows = rows.Where(e => e.EntryDate <= to);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(e => e.EntryNo.ToLower().Contains(term) ||
                                       e.Narration.ToLower().Contains(term) ||
                                       (e.ReferenceNo != null && e.ReferenceNo.ToLower().Contains(term)));
            }

            var total = await rows.CountAsync();

            /* Counted over the WHOLE filter, not the page. A card that only
               counted the visible rows would change every time somebody turned
               a page, which is not what "12 drafts" is supposed to mean.

               Reversed is counted by the LINK, not by the status: a reversed
               entry deliberately stays POSTED so the pair cancels in the
               statements, so status alone can no longer find them. */
            var postedCount = await rows.CountAsync(e => e.Status.StatusKey == Posted);
            var draftCount = await rows.CountAsync(e => e.Status.StatusKey == "DRAFT");
            var reversedCount = await rows.CountAsync(e => e.ReversedByEntryId != null);

            var items = await rows
                .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.EntryId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(e => new
                {
                    id = e.EntryId,
                    entryNo = e.EntryNo,
                    entryDate = e.EntryDate,
                    entryType = e.EntryType.TypeKey,
                    entryTypeName = e.EntryType.TypeName,
                    reference = e.ReferenceNo,
                    location = e.Location.LocationName,
                    narration = e.Narration,
                    status = e.Status.StatusKey,
                    statusName = e.Status.StatusName,
                    createdBy = e.CreatedByUser.FullName,
                    postedBy = e.PostedByUser != null ? e.PostedByUser.FullName : null,
                    /* A reversed entry stays POSTED so the pair cancels in the
                       statements, so the status alone cannot tell the screen it
                       was undone. This is what does. */
                    reversedById = e.ReversedByEntryId,
                    reversedBy = e.ReversedByEntry != null ? e.ReversedByEntry.EntryNo : null,
                    totalDebit = e.JournalEntryLines.Sum(l => (decimal?)l.DebitAmount) ?? 0m,
                    totalCredit = e.JournalEntryLines.Sum(l => (decimal?)l.CreditAmount) ?? 0m
                })
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                pageCount = (int)Math.Ceiling(total / (double)pageSize),
                postedCount,
                draftCount,
                reversedCount,
                items
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load journal entries");
        }
    }

    [HttpGet("journal-entries/{id:int}")]
    public async Task<IActionResult> GetJournalEntry(int id)
    {
        try
        {
            var e = await _db.JournalEntries.AsNoTracking()
                .Where(x => x.EntryId == id)
                .Select(x => new
                {
                    id = x.EntryId,
                    entryNo = x.EntryNo,
                    entryDate = x.EntryDate,
                    entryType = x.EntryType.TypeKey,
                    entryTypeName = x.EntryType.TypeName,
                    reference = x.ReferenceNo,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    period = x.Period.PeriodName,
                    narration = x.Narration,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.FullName,
                    createdAt = x.CreatedAt,
                    postedBy = x.PostedByUser != null ? x.PostedByUser.FullName : null,
                    reversedById = x.ReversedByEntryId,
                    reversedBy = x.ReversedByEntry != null ? x.ReversedByEntry.EntryNo : null,
                    /* The other direction: this entry is itself the mirror that
                       undid something. The screen shows "reverses JV-26-0180". */
                    reversesId = x.Reverses.Select(r => (int?)r.EntryId).FirstOrDefault(),
                    reverses = x.Reverses.Select(r => r.EntryNo).FirstOrDefault(),
                    lines = x.JournalEntryLines.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.LineId,
                        lineNo = l.LineNo,
                        accountId = l.AccountId,
                        accountCode = l.Account.AccountCode,
                        accountName = l.Account.AccountName,
                        partyId = l.PartyUserId,
                        partyName = l.PartyUser != null ? l.PartyUser.LegalName : null,
                        description = l.Description,
                        debit = l.DebitAmount,
                        credit = l.CreditAmount
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (e is null) return NotFound(new { message = $"No journal entry with id {id}." });

            return Ok(new
            {
                e.id, e.entryNo, e.entryDate, e.entryType, e.entryTypeName,
                e.reference, e.locationId, e.location, e.period, e.narration,
                e.status, e.statusName, e.createdBy, e.createdAt, e.postedBy,
                e.reversedById, e.reversedBy, e.reversesId, e.reverses,
                totalDebit = e.lines.Sum(l => l.debit),
                totalCredit = e.lines.Sum(l => l.credit),
                e.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load journal entry {id}");
        }
    }

    /// <summary>
    /// Posts a manual journal entry. Debits must equal credits -- that check is
    /// the whole point of double entry and is enforced here, not in the browser.
    /// </summary>
    [HttpPost("journal-entries")]
    public async Task<IActionResult> CreateJournalEntry([FromBody] JournalEntryRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count < 2)
                return BadRequest(new { message = "A journal entry needs at least two lines." });

            var totalDebit = body.Lines.Sum(l => l.Debit);
            var totalCredit = body.Lines.Sum(l => l.Credit);
            if (totalDebit != totalCredit)
                return BadRequest(new
                {
                    message = $"Entry is out of balance: debits {totalDebit:N2} against credits {totalCredit:N2}."
                });
            if (totalDebit == 0)
                return BadRequest(new { message = "An entry of zero has nothing to post." });

            foreach (var l in body.Lines)
            {
                if (l.Debit < 0 || l.Credit < 0)
                    return BadRequest(new { message = "Debit and credit cannot be negative." });
                if (l.Debit > 0 && l.Credit > 0)
                    return BadRequest(new { message = "A line is either a debit or a credit, never both." });
                if (!await _db.Accounts.AnyAsync(a => a.AccountId == l.AccountId && !a.IsGroup))
                    return BadRequest(new { message = $"Account {l.AccountId} is missing, or is a group heading that cannot take a posting." });
            }

            var date = body.EntryDate ?? Today();
            var period = await _db.FiscalPeriods
                .FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (period is null)
                return BadRequest(new { message = $"No fiscal period covers {date:yyyy-MM-dd}." });
            if (period.IsClosed)
                return BadRequest(new { message = $"{period.PeriodName} is closed. Reopen it or use another date." });

            var draft = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            /* JOURNAL, not MANUAL. There is no MANUAL row in JournalEntryType,
               and FirstOrDefaultAsync does not complain about that -- the
               fallback then handed every hand-written entry the FIRST type in
               the table, which is SALE. Entries typed as sales that were never
               sales. */
            var type = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "JOURNAL")
                       ?? await _db.JournalEntryTypes.FirstAsync();

            await using var tx = await _db.Database.BeginTransactionAsync();

            var entry = new JournalEntry
            {
                EntryNo = await NextNumber("JV"),
                EntryDate = date,
                EntryTypeId = type.EntryTypeId,
                PeriodId = period.PeriodId,
                LocationId = body.LocationId,
                ReferenceNo = body.Reference,
                Narration = body.Narration ?? "",
                StatusId = body.PostImmediately
                    ? posted?.StatusId ?? draft!.StatusId
                    : draft?.StatusId ?? 1,
                CreatedByUserId = CurrentUserId(),
                PostedByUserId = body.PostImmediately ? CurrentUserId() : null,
                CreatedAt = Today()
            };
            _db.JournalEntries.Add(entry);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    EntryId = entry.EntryId,
                    LineNo = n++,
                    AccountId = l.AccountId,
                    PartyUserId = l.PartyId,
                    Description = l.Description,
                    DebitAmount = l.Debit,
                    CreditAmount = l.Credit
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log(body.PostImmediately ? "JOURNAL_POSTED" : "JOURNAL_DRAFTED",
                "JournalEntry", entry.EntryNo, $"{totalDebit:N2} over {body.Lines.Count} lines", 2);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "journal-entry", entry.EntryId, CurrentUserId());

            return Ok(new
            {
                id = entry.EntryId,
                entryNo = entry.EntryNo,
                message = $"Entry {entry.EntryNo} {(body.PostImmediately ? "posted" : "saved as draft")}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the journal entry");
        }
    }

    [HttpPost("journal-entries/{id:int}/post")]
    public async Task<IActionResult> PostJournalEntry(int id)
    {
        try
        {
            var entry = await _db.JournalEntries
                .Include(e => e.JournalEntryLines)
                .Include(e => e.Status)
                .FirstOrDefaultAsync(e => e.EntryId == id);

            if (entry is null) return NotFound(new { message = $"No journal entry with id {id}." });
            if (entry.Status.StatusKey == Posted)
                return BadRequest(new { message = $"{entry.EntryNo} is already posted." });

            var d = entry.JournalEntryLines.Sum(l => l.DebitAmount);
            var c = entry.JournalEntryLines.Sum(l => l.CreditAmount);
            if (d != c)
                return BadRequest(new { message = $"{entry.EntryNo} is out of balance and cannot be posted." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            if (posted is null) return BadRequest(new { message = "No POSTED status is configured." });

            entry.StatusId = posted.StatusId;
            entry.PostedByUserId = CurrentUserId();
            await _db.SaveChangesAsync();
            await Log("JOURNAL_POSTED", "JournalEntry", entry.EntryNo, $"{d:N2}", 2);

            /* -- C4 -- */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.JournalPosted,
                $"Entry posted by {CurrentUserName()}",
                $"{entry.EntryNo} -- PKR {d:N0} over {entry.JournalEntryLines.Count} lines.",
                url: $"/accounting/journal-entries/{entry.EntryId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{entry.EntryNo} posted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"post journal entry {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  VOUCHERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("vouchers")]
    public async Task<IActionResult> GetVouchers(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 500) pageSize = 50;

            var rows = _db.Vouchers.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(v => v.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(type)) rows = rows.Where(v => v.VoucherType.TypeCode == type);
            if (from is not null) rows = rows.Where(v => v.VoucherDate >= from);
            if (to is not null) rows = rows.Where(v => v.VoucherDate <= to);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(v => v.VoucherNo.ToLower().Contains(term) ||
                                       v.Narration.ToLower().Contains(term) ||
                                       (v.PartyUser != null && v.PartyUser.LegalName.ToLower().Contains(term)));
            }

            var count = await rows.CountAsync();
            var money = await rows.SumAsync(v => (decimal?)v.Amount) ?? 0m;

            /* Split by direction over the WHOLE filter. The screen shows money
               in and money out as two figures, and working them out from the
               page on screen would give a different answer on every page. Only
               POSTED counts -- a draft voucher has not moved anything. */
            var receiptTotal = await rows
                .Where(v => v.VoucherType.IsReceipt && v.Status.StatusKey == Posted)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var paymentTotal = await rows
                .Where(v => !v.VoucherType.IsReceipt && v.Status.StatusKey == Posted)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var draftCount = await rows.CountAsync(v => v.Status.StatusKey == "DRAFT");

            var items = await rows
                .OrderByDescending(v => v.VoucherDate).ThenByDescending(v => v.VoucherId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(v => new
                {
                    id = v.VoucherId,
                    voucherNo = v.VoucherNo,
                    type = v.VoucherType.TypeCode,
                    typeName = v.VoucherType.TypeName,
                    isReceipt = v.VoucherType.IsReceipt,
                    date = v.VoucherDate,
                    location = v.Location.LocationName,
                    partyId = v.PartyUserId,
                    partyName = v.PartyUser != null ? v.PartyUser.LegalName : null,
                    cashBankAccount = v.CashBankAccount != null ? v.CashBankAccount.AccountName : null,
                    amount = v.Amount,
                    paymentMethod = v.Method.MethodKey,
                    paymentProvider = v.PaymentProvider,
                    reference = v.ReferenceNo,
                    narration = v.Narration,
                    status = v.Status.StatusKey,
                    statusName = v.Status.StatusName,
                    createdBy = v.CreatedByUser.FullName
                })
                .ToListAsync();

            /* `count` and `total` are over the WHOLE filter, not the page --
               the summary cards on the screen show the filtered set, and a card
               that only counted the visible page would be wrong the moment
               somebody turned to page two. */
            return Ok(new
            {
                count,
                total = money,
                receiptTotal,
                paymentTotal,
                draftCount,
                page,
                pageSize,
                pageCount = (int)Math.Ceiling(count / (double)pageSize),
                items
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load vouchers");
        }
    }

    [HttpGet("vouchers/{id:int}")]
    public async Task<IActionResult> GetVoucher(int id)
    {
        try
        {
            var v = await _db.Vouchers.AsNoTracking()
                .Where(x => x.VoucherId == id)
                .Select(x => new
                {
                    id = x.VoucherId,
                    voucherNo = x.VoucherNo,
                    type = x.VoucherType.TypeCode,
                    typeName = x.VoucherType.TypeName,
                    isReceipt = x.VoucherType.IsReceipt,
                    date = x.VoucherDate,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    partyId = x.PartyUserId,
                    partyName = x.PartyUser != null ? x.PartyUser.LegalName : null,
                    cashBankAccountId = x.CashBankAccountId,
                    cashBankAccount = x.CashBankAccount != null ? x.CashBankAccount.AccountName : null,
                    amount = x.Amount,
                    paymentMethod = x.Method.MethodKey,
                    paymentProvider = x.PaymentProvider,
                    reference = x.ReferenceNo,
                    walletTxnId = x.WalletTxnId,
                    narration = x.Narration,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    entryId = x.EntryId,
                    entryNo = x.Entry != null ? x.Entry.EntryNo : null,
                    reversalEntryNo = x.Entry != null && x.Entry.ReversedByEntry != null
                        ? x.Entry.ReversedByEntry.EntryNo : null,
                    createdBy = x.CreatedByUser.FullName,
                    allocations = x.VoucherAllocations.Select(a => new
                    {
                        id = a.AllocationId,
                        salesInvoiceId = a.SalesInvoiceId,
                        purchaseInvoiceId = a.PurchaseInvoiceId,
                        amount = a.Amount
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (v is null) return NotFound(new { message = $"No voucher with id {id}." });
            return Ok(v);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load voucher {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  COLLECTIONS  --  the control gap
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Field collections. Anything still AWAITING has been taken by a rep but
    /// NOT yet confirmed by Accounts, and deliberately has not moved the
    /// customer ledger. That is what stops a rep sitting on cash while the books
    /// look settled.
    /// </summary>
    [HttpGet("collections")]
    public async Task<IActionResult> GetCollections([FromQuery] string? status, [FromQuery] int? customerId)
    {
        try
        {
            var rows = _db.Collections.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(c => c.Status.StatusKey == status);
            if (customerId is not null) rows = rows.Where(c => c.CustomerUserId == customerId);

            var items = await rows
                .OrderByDescending(c => c.CollectedOn).ThenByDescending(c => c.CollectionId)
                .Select(c => new
                {
                    id = c.CollectionId,
                    receiptNo = c.ReceiptNo,
                    customerId = c.CustomerUserId,
                    customerName = c.CustomerUser.LegalName,
                    collectedBy = c.CollectedByUser.User.FullName,
                    collectedOn = c.CollectedOn,
                    amount = c.Amount,
                    method = c.Method.MethodKey,
                    methodName = c.Method.MethodName,
                    reference = c.ReferenceNo,
                    bank = c.BankName,
                    chequeDate = c.ChequeDate,
                    status = c.Status.StatusKey,
                    statusName = c.Status.StatusName,
                    confirmedOn = c.ConfirmedOn,
                    confirmedBy = c.ConfirmedByUser != null ? c.ConfirmedByUser.User.FullName : null,
                    note = c.Note,
                    against = c.CollectionAllocations.Select(a => a.Order.OrderNo).ToList()
                })
                .ToListAsync();

            var shaped = items.Select(c => new
            {
                c.id, c.receiptNo, c.customerId, c.customerName,
                customerInitials = Initials(c.customerName),
                c.collectedBy, c.collectedOn, c.amount, c.method, c.methodName,
                c.reference, c.bank, c.chequeDate, c.status, c.statusName,
                c.confirmedOn, c.confirmedBy, c.note, c.against
            }).ToList();

            return Ok(new
            {
                awaitingCount = shaped.Count(c => c.status == "AWAITING"),
                awaitingTotal = shaped.Where(c => c.status == "AWAITING").Sum(c => c.amount),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load collections");
        }
    }

    /// <summary>
    /// Accounts confirms a rep's collection. This is the moment the money
    /// becomes real to the books.
    /// </summary>
    [HttpPost("collections/{id:int}/confirm")]
    public async Task<IActionResult> ConfirmCollection(int id)
    {
        try
        {
            var c = await _db.Collections
                .Include(x => x.Status)
                .FirstOrDefaultAsync(x => x.CollectionId == id);

            if (c is null) return NotFound(new { message = $"No collection with id {id}." });
            if (c.Status.StatusKey == "CONFIRMED")
                return BadRequest(new { message = $"{c.ReceiptNo} was already confirmed." });

            var confirmed = await _db.CollectionStatuses.FirstOrDefaultAsync(s => s.StatusKey == "CONFIRMED");
            if (confirmed is null) return BadRequest(new { message = "No CONFIRMED status is configured." });

            c.StatusId = confirmed.StatusId;
            c.ConfirmedOn = Today();
            c.ConfirmedByUserId = CurrentUserId();
            await _db.SaveChangesAsync();
            await Log("COLLECTION_CONFIRMED", "Collection", c.ReceiptNo, $"{c.Amount:N2}", 2);

            /* -- C8 -- addressed to the rep who physically took the cash and is
               answerable for it until this happens. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CollectionConfirmed,
                $"Collection confirmed by {CurrentUserName()}",
                $"{c.ReceiptNo} -- PKR {c.Amount:N0} has been confirmed.",
                url: "/accounting/collections",
                exceptUserId: CurrentUserId(),
                alsoUserIds: new[] { c.CollectedByUserId });

            return Ok(new { id, message = $"{c.ReceiptNo} confirmed." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"confirm collection {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  EXPENSES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("expenses")]
    public async Task<IActionResult> GetExpenses(
        [FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 500) pageSize = 50;

            var rows = _db.Expenses.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(e => e.Status.StatusKey == status);
            if (from is not null) rows = rows.Where(e => e.ExpenseDate >= from);
            if (to is not null) rows = rows.Where(e => e.ExpenseDate <= to);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(e => e.ExpenseNo.ToLower().Contains(term) ||
                                       e.VendorName.ToLower().Contains(term) ||
                                       e.CategoryName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseId)
                .Select(e => new
                {
                    id = e.ExpenseId,
                    expenseNo = e.ExpenseNo,
                    expenseDate = e.ExpenseDate,
                    location = e.Location.LocationName,
                    categoryName = e.CategoryName,
                    expenseAccount = e.ExpenseAccount.AccountName,
                    paidFromAccount = e.PaidFromAccount.AccountName,
                    amount = e.Amount,
                    vendorName = e.VendorName,
                    paymentMethod = e.Method.MethodKey,
                    description = e.Description,
                    status = e.Status.StatusKey,
                    statusName = e.Status.StatusName,
                    createdBy = e.CreatedByUser.FullName
                })
                .ToListAsync();

            /* `total` is the money and `count` is the row count -- both are what
               the summary cards on the screen show, and both are over the WHOLE
               filter, not just the page. `items` is the page. */
            var paged = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            /* The biggest category across the WHOLE filter, not the page on
               screen. The card used to say "(this page)" because the browser
               could only see the rows it had been handed -- honest, but it
               answered a question nobody had asked. */
            var topCategory = items
                .Where(e => e.status != "REJECTED" && e.status != "CANCELLED")
                .GroupBy(e => string.IsNullOrWhiteSpace(e.categoryName) ? "Uncategorised" : e.categoryName)
                .Select(g => new { name = g.Key, amount = g.Sum(x => x.amount) })
                .OrderByDescending(g => g.amount)
                .FirstOrDefault();

            return Ok(new
            {
                total = items.Sum(e => e.amount),
                count = items.Count,
                page,
                pageSize,
                pageCount = (int)Math.Ceiling(items.Count / (double)pageSize),
                topCategory,
                draftCount = items.Count(e => e.status == "DRAFT"),
                items = paged
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load expenses");
        }
    }

    [HttpGet("expenses/{id:int}")]
    public async Task<IActionResult> GetExpense(int id)
    {
        try
        {
            var e = await _db.Expenses.AsNoTracking()
                .Where(x => x.ExpenseId == id)
                .Select(x => new
                {
                    id = x.ExpenseId,
                    expenseNo = x.ExpenseNo,
                    expenseDate = x.ExpenseDate,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    categoryName = x.CategoryName,
                    expenseAccountId = x.ExpenseAccountId,
                    expenseAccount = x.ExpenseAccount.AccountName,
                    paidFromAccountId = x.PaidFromAccountId,
                    paidFromAccount = x.PaidFromAccount.AccountName,
                    amount = x.Amount,
                    vendorName = x.VendorName,
                    paymentMethod = x.Method.MethodKey,
                    description = x.Description,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    entryId = x.EntryId,
                    entryNo = x.Entry != null ? x.Entry.EntryNo : null,
                    /* Set once the expense has been reversed, so the screen can
                       say which entry undid it rather than only that it was. */
                    reversalEntryNo = x.Entry != null && x.Entry.ReversedByEntry != null
                        ? x.Entry.ReversedByEntry.EntryNo : null,
                    createdBy = x.CreatedByUser.FullName
                })
                .FirstOrDefaultAsync();

            if (e is null) return NotFound(new { message = $"No expense with id {id}." });
            return Ok(e);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load expense {id}");
        }
    }

    [HttpPost("expenses")]
    public async Task<IActionResult> CreateExpense([FromBody] ExpenseRequest body)
    {
        try
        {
            /* One validator for create and update. Two copies of the same
               rules is two sets of rules the day somebody edits one of them. */
            var invalid = await ValidateExpense(body);
            if (invalid is not null) return BadRequest(new { message = invalid });

            var draft = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");

            var e = new Expense
            {
                ExpenseNo = await NextNumber("EXP"),
                ExpenseDate = body.ExpenseDate ?? Today(),
                LocationId = body.LocationId,
                CategoryName = body.CategoryName ?? "General",
                ExpenseAccountId = body.ExpenseAccountId,
                PaidFromAccountId = body.PaidFromAccountId,
                Amount = body.Amount,
                VendorName = body.VendorName.Trim(),
                MethodId = body.MethodId,
                Description = body.Description,
                StatusId = draft?.StatusId ?? 1,
                CreatedByUserId = CurrentUserId()
            };
            _db.Expenses.Add(e);
            await _db.SaveChangesAsync();
            await Log("EXPENSE_CREATED", "Expense", e.ExpenseNo, $"{e.Amount:N2} to {e.VendorName}", 1);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "expense", e.ExpenseId, CurrentUserId());

            /* -- C1 -- someone has to approve this before it reaches the
               ledger, so the people who can approve it are told. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.ExpenseCreated,
                $"Expense filed by {CurrentUserName()}",
                $"{e.ExpenseNo} -- {e.VendorName}, PKR {e.Amount:N0}. Waiting for approval.",
                url: $"/accounting/expenses/{e.ExpenseId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id = e.ExpenseId, expenseNo = e.ExpenseNo, message = $"Expense {e.ExpenseNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the expense");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Trial balance: every posting account with its debit/credit total.</summary>
    [HttpGet("trial-balance")]
    public async Task<IActionResult> GetTrialBalance([FromQuery] DateOnly? asOf)
    {
        try
        {
            var cutoff = asOf ?? Today();

            var rows = await _db.Accounts.AsNoTracking()
                .Where(a => !a.IsGroup)
                .Select(a => new
                {
                    id = a.AccountId,
                    code = a.AccountCode,
                    name = a.AccountName,
                    type = a.AccountType.TypeName,
                    group = a.AccountType.Group.GroupName,
                    isDebitNormal = a.AccountType.IsDebitNormal,
                    opening = a.OpeningBalance,
                    debit = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == Posted && l.Entry.EntryDate <= cutoff)
                        .Sum(l => (decimal?)l.DebitAmount) ?? 0m,
                    credit = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == Posted && l.Entry.EntryDate <= cutoff)
                        .Sum(l => (decimal?)l.CreditAmount) ?? 0m
                })
                .ToListAsync();

            var lines = rows.Select(r =>
            {
                var net = ToDebitBasis(r.opening, r.isDebitNormal) + r.debit - r.credit;
                return new
                {
                    r.id, r.code, r.name, r.type, r.group, r.opening, r.debit, r.credit,
                    debitBalance = net > 0 ? net : 0m,
                    creditBalance = net < 0 ? -net : 0m
                };
            }).Where(r => r.debit != 0 || r.credit != 0 || r.opening != 0)
              .OrderBy(r => r.code).ToList();

            /* Split the two halves apart. Posted MOVEMENT is double entry and
               must balance to the cent -- if it does not, something is wrong
               with the books. OPENING balances are typed in by hand when the
               system is set up and frequently do not balance, which is a data
               problem, not an accounting one.

               Reporting one combined "isBalanced: false" hides which of the two
               it is and sends people hunting through journal entries that were
               never at fault. */
            var movementDebit = rows.Sum(r => r.debit);
            var movementCredit = rows.Sum(r => r.credit);
            var openingNet = rows.Sum(r => ToDebitBasis(r.opening, r.isDebitNormal));

            return Ok(new
            {
                asOf = cutoff,
                totalDebit = lines.Sum(l => l.debitBalance),
                totalCredit = lines.Sum(l => l.creditBalance),
                isBalanced = lines.Sum(l => l.debitBalance) == lines.Sum(l => l.creditBalance),

                movementDebit,
                movementCredit,
                movementBalances = movementDebit == movementCredit,

                openingImbalance = openingNet,
                openingBalances = openingNet == 0m,

                note = openingNet == 0m
                    ? null
                    : $"Posted movement balances ({movementDebit:N2} = {movementCredit:N2}). " +
                      $"The opening balances carry a {Math.Abs(openingNet):N2} " +
                      $"{(openingNet > 0 ? "debit" : "credit")} imbalance, which is a setup " +
                      $"data issue and not a posting error.",
                lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the trial balance");
        }
    }

    /// <summary>Profit and loss for a date range, grouped by account type.</summary>
    [HttpGet("profit-loss")]
    public async Task<IActionResult> GetProfitLoss([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var start = from ?? new DateOnly(Today().Year, 1, 1);
            var end = to ?? Today();

            var rows = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.Entry.Status.StatusKey == Posted &&
                            l.Entry.EntryDate >= start && l.Entry.EntryDate <= end &&
                            (l.Account.AccountType.Group.GroupName == GroupRevenue ||
                             l.Account.AccountType.Group.GroupName == GroupExpenses))
                .GroupBy(l => new
                {
                    l.Account.AccountId,
                    l.Account.AccountCode,
                    l.Account.AccountName,
                    Group = l.Account.AccountType.Group.GroupName,
                    Type = l.Account.AccountType.TypeName
                })
                .Select(g => new
                {
                    id = g.Key.AccountId,
                    code = g.Key.AccountCode,
                    name = g.Key.AccountName,
                    group = g.Key.Group,
                    type = g.Key.Type,
                    debit = g.Sum(l => l.DebitAmount),
                    credit = g.Sum(l => l.CreditAmount)
                })
                .ToListAsync();

            var income = rows.Where(r => r.group == GroupRevenue)
                .Select(r => new { r.id, r.code, r.name, r.type, amount = r.credit - r.debit })
                .OrderBy(r => r.code).ToList();
            var expense = rows.Where(r => r.group == GroupExpenses)
                .Select(r => new { r.id, r.code, r.name, r.type, amount = r.debit - r.credit })
                .OrderBy(r => r.code).ToList();

            var totalIncome = income.Sum(i => i.amount);
            var totalExpense = expense.Sum(e => e.amount);

            return Ok(new
            {
                from = start,
                to = end,
                income,
                expense,
                totalIncome,
                totalExpense,
                netProfit = totalIncome - totalExpense
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the profit and loss statement");
        }
    }

    /// <summary>Balance sheet as at a date: assets against liabilities + equity.</summary>
    [HttpGet("balance-sheet")]
    public async Task<IActionResult> GetBalanceSheet([FromQuery] DateOnly? asOf)
    {
        try
        {
            var cutoff = asOf ?? Today();

            var rows = await _db.Accounts.AsNoTracking()
                .Where(a => !a.IsGroup && a.AccountType.Group.OnBalanceSheet)
                .Select(a => new
                {
                    id = a.AccountId,
                    code = a.AccountCode,
                    name = a.AccountName,
                    group = a.AccountType.Group.GroupName,
                    type = a.AccountType.TypeName,
                    isDebitNormal = a.AccountType.IsDebitNormal,
                    opening = a.OpeningBalance,
                    movement = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == Posted && l.Entry.EntryDate <= cutoff)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m
                })
                .ToListAsync();

            var shaped = rows.Select(r => new
            {
                r.id, r.code, r.name, r.group, r.type,
                balance = r.isDebitNormal ? r.opening + r.movement : r.opening - r.movement
            }).Where(r => r.balance != 0).OrderBy(r => r.code).ToList();

            var assets = shaped.Where(r => r.group == GroupAssets).ToList();
            var liabilities = shaped.Where(r => r.group == GroupLiabilities).ToList();
            var equity = shaped.Where(r => r.group == GroupCapital).ToList();

            var totalAssets = assets.Sum(a => a.balance);
            var totalLiabilities = liabilities.Sum(l => l.balance);
            var totalEquity = equity.Sum(e => e.balance);

            return Ok(new
            {
                asOf = cutoff,
                assets, liabilities, equity,
                totalAssets, totalLiabilities, totalEquity,
                /* Retained earnings are not a stored account here, so the gap
                   between the two sides is reported rather than hidden -- a
                   balance sheet that silently balances is worse than one that
                   shows you the difference. */
                difference = totalAssets - (totalLiabilities + totalEquity)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the balance sheet");
        }
    }

    /// <summary>Cash movement over a range, per cash/bank account.</summary>
    [HttpGet("cash-flow")]
    public async Task<IActionResult> GetCashFlow([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var start = from ?? new DateOnly(Today().Year, 1, 1);
            var end = to ?? Today();

            var rows = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.Entry.Status.StatusKey == Posted &&
                            l.Entry.EntryDate >= start && l.Entry.EntryDate <= end &&
                            (l.Account.AccountType.TypeName.Contains("Cash") ||
                             l.Account.AccountType.TypeName.Contains("Bank")))
                .GroupBy(l => new { l.Account.AccountId, l.Account.AccountCode, l.Account.AccountName })
                .Select(g => new
                {
                    id = g.Key.AccountId,
                    code = g.Key.AccountCode,
                    name = g.Key.AccountName,
                    inflow = g.Sum(l => l.DebitAmount),
                    outflow = g.Sum(l => l.CreditAmount)
                })
                .ToListAsync();

            var lines = rows.Select(r => new
            {
                r.id, r.code, r.name, r.inflow, r.outflow,
                net = r.inflow - r.outflow
            }).OrderBy(r => r.code).ToList();

            return Ok(new
            {
                from = start,
                to = end,
                totalInflow = lines.Sum(l => l.inflow),
                totalOutflow = lines.Sum(l => l.outflow),
                netChange = lines.Sum(l => l.net),
                lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the cash-flow statement");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PERIODS AND RECONCILIATION
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("periods")]
    public async Task<IActionResult> GetPeriods()
    {
        try
        {
            return Ok(await _db.FiscalPeriods.AsNoTracking()
                .OrderByDescending(p => p.PeriodYear).ThenByDescending(p => p.PeriodMonth)
                .Select(p => new
                {
                    id = p.PeriodId,
                    name = p.PeriodName,
                    year = p.PeriodYear,
                    month = p.PeriodMonth,
                    startDate = p.StartDate,
                    endDate = p.EndDate,
                    isClosed = p.IsClosed,
                    closedAt = p.ClosedAt,
                    closedBy = p.ClosedByUser != null ? p.ClosedByUser.FullName : null,
                    entryCount = p.JournalEntries.Count,
                    draftCount = p.JournalEntries.Count(e => e.Status.StatusKey != Posted)
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load fiscal periods");
        }
    }

    /// <summary>
    /// Closes a period. Refuses while any entry in it is still a draft -- closing
    /// over an unposted entry is how a month ends up permanently wrong.
    /// </summary>
    [HttpPost("periods/{id:int}/close")]
    public async Task<IActionResult> ClosePeriod(int id)
    {
        try
        {
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.PeriodId == id);
            if (period is null) return NotFound(new { message = $"No period with id {id}." });
            if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is already closed." });

            var drafts = await _db.JournalEntries
                .CountAsync(e => e.PeriodId == id && e.Status.StatusKey != Posted);
            if (drafts > 0)
                return BadRequest(new
                {
                    message = $"{period.PeriodName} still has {drafts} unposted " +
                              $"{(drafts == 1 ? "entry" : "entries")}. Post or delete them first."
                });

            period.IsClosed = true;
            period.ClosedAt = Today();
            period.ClosedByUserId = CurrentUserId();
            await _db.SaveChangesAsync();
            await Log("PERIOD_CLOSED", "FiscalPeriod", period.PeriodName, null, 3);

            /* -- C9 -- anybody about to book something into that month needs to
               know they no longer can. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.PeriodChanged,
                $"Period closed by {CurrentUserName()}",
                $"{period.PeriodName} is closed. No further entries can be made in it.",
                url: "/accounting/period-close",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{period.PeriodName} closed." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"close period {id}");
        }
    }

    /// <summary>
    /// Reopens a closed period so a backdated correction can be posted.
    ///
    /// This is deliberately available rather than permanent: a month closed a
    /// day early is a normal mistake, and the alternative is people posting the
    /// correction into the wrong month, which is worse and much harder to spot
    /// later. It is logged at warning severity with the name of whoever did it,
    /// so /admin/audit-log shows every reopen.
    /// </summary>
    [HttpPost("periods/{id:int}/reopen")]
    public async Task<IActionResult> ReopenPeriod(int id)
    {
        try
        {
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.PeriodId == id);
            if (period is null) return NotFound(new { message = $"No period with id {id}." });
            if (!period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is already open." });

            /* A period cannot be reopened while a LATER one is still closed --
               otherwise a correction lands in a month that a closed month has
               already carried forward. */
            var laterClosed = await _db.FiscalPeriods
                .Where(p => p.IsClosed &&
                           (p.PeriodYear > period.PeriodYear ||
                           (p.PeriodYear == period.PeriodYear && p.PeriodMonth > period.PeriodMonth)))
                .OrderBy(p => p.PeriodYear).ThenBy(p => p.PeriodMonth)
                .Select(p => p.PeriodName)
                .FirstOrDefaultAsync();

            if (laterClosed is not null)
                return BadRequest(new
                {
                    message = $"{laterClosed} is closed and comes after {period.PeriodName}. " +
                              $"Reopen the later period first."
                });

            period.IsClosed = false;
            period.ClosedAt = null;
            period.ClosedByUserId = null;
            await _db.SaveChangesAsync();
            await Log("PERIOD_REOPENED", "FiscalPeriod", period.PeriodName,
                      "Backdated postings allowed again", 3);

            return Ok(new { id, message = $"{period.PeriodName} reopened." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reopen period {id}");
        }
    }

    [HttpGet("reconciliation")]
    public async Task<IActionResult> GetReconciliation()
    {
        try
        {
            return Ok(await _db.BankReconciliations.AsNoTracking()
                .OrderByDescending(r => r.StatementDate)
                .Select(r => new
                {
                    id = r.ReconciliationId,
                    accountId = r.AccountId,
                    accountName = r.Account.AccountName,
                    statementDate = r.StatementDate,
                    openingBalance = r.OpeningBalance,
                    closingBalance = r.ClosingBalance,
                    status = r.Status.StatusKey,
                    statusName = r.Status.StatusName,
                    finalizedOn = r.FinalizedOn,
                    preparedBy = r.PreparedByUser.User.FullName,
                    lineCount = r.BankStatementLines.Count,

                    /* "Matched" is not a flag on the line -- a statement line is
                       matched when MatchedLineId points at a ledger line. Reading
                       it off the FK means there is no second copy of the truth. */
                    matchedCount = r.BankStatementLines.Count(l => l.MatchedLineId != null),
                    unmatchedTotal = r.BankStatementLines
                        .Where(l => l.MatchedLineId == null)
                        .Sum(l => (decimal?)l.Amount) ?? 0m
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load bank reconciliations");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One reconciliation opened up: the bank statement lines on the left, and
    /// the POSTED ledger lines on this account over the same window on the
    /// right, which is what the screen matches against each other.
    ///
    /// A statement line is matched when its MatchedLineId points at a journal
    /// entry line -- there is no separate "isMatched" flag, so there is no
    /// second copy of the truth to fall out of step.
    /// </summary>
    [HttpGet("reconciliation/{id:int}")]
    public async Task<IActionResult> GetReconciliationDetail(int id)
    {
        try
        {
            var recon = await _db.BankReconciliations.AsNoTracking()
                .Include(r => r.Account)
                .Include(r => r.Status)
                .Include(r => r.PreparedByUser).ThenInclude(e => e.User)
                .FirstOrDefaultAsync(r => r.ReconciliationId == id);

            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });

            var statementLines = await _db.BankStatementLines.AsNoTracking()
                .Where(l => l.ReconciliationId == id)
                .OrderByDescending(l => l.LineDate).ThenBy(l => l.StatementLineId)
                .Select(l => new
                {
                    id = l.StatementLineId,
                    date = l.LineDate,
                    description = l.Description,
                    amount = l.Amount,
                    matchedLineId = l.MatchedLineId
                })
                .ToListAsync();

            /* Candidate ledger lines: posted journal lines on the same account,
               within a month either side of the statement date. Anything already
               claimed by ANOTHER reconciliation is left out so two statements
               cannot both take the same ledger line. */
            var from = recon.StatementDate.AddMonths(-1);
            var to = recon.StatementDate.AddMonths(1);

            var claimedElsewhere = await _db.BankStatementLines.AsNoTracking()
                .Where(l => l.ReconciliationId != id && l.MatchedLineId != null)
                .Select(l => l.MatchedLineId!.Value)
                .ToListAsync();

            var ledgerLines = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.AccountId == recon.AccountId
                         && l.Entry.Status.StatusKey == Posted
                         && l.Entry.EntryDate >= from && l.Entry.EntryDate <= to
                         && !claimedElsewhere.Contains(l.LineId))
                .OrderByDescending(l => l.Entry.EntryDate).ThenBy(l => l.LineId)
                .Select(l => new
                {
                    id = l.LineId,
                    date = l.Entry.EntryDate,
                    entryNo = l.Entry.EntryNo,
                    entryType = l.Entry.EntryType.TypeName,
                    description = l.Description,
                    party = l.PartyUser != null ? l.PartyUser.LegalName : null,
                    /* Signed the way a bank statement reads it: money into the
                       bank account is a debit in the ledger and a credit on the
                       statement, so a positive amount means both. */
                    amount = l.DebitAmount - l.CreditAmount
                })
                .ToListAsync();

            return Ok(new
            {
                id = recon.ReconciliationId,
                accountId = recon.AccountId,
                accountName = recon.Account.AccountName,
                accountCode = recon.Account.AccountCode,
                statementDate = recon.StatementDate,
                openingBalance = recon.OpeningBalance,
                closingBalance = recon.ClosingBalance,
                status = recon.Status.StatusKey,
                statusName = recon.Status.StatusName,
                finalizedOn = recon.FinalizedOn,
                preparedBy = recon.PreparedByUser.User.FullName,
                statementLines,
                ledgerLines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load reconciliation {id}");
        }
    }

    /// <summary>
    /// Ties one statement line to one ledger line, or unties it when
    /// JournalEntryLineId is null. Refuses once the reconciliation is finalised
    /// -- that is the whole point of finalising it.
    /// </summary>
    [HttpPost("reconciliation/{id:int}/match")]
    public async Task<IActionResult> MatchReconciliationLine(int id, [FromBody] MatchLineRequest body)
    {
        try
        {
            var recon = await _db.BankReconciliations
                .Include(r => r.Status)
                .FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });

            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is finalised and cannot be changed." });

            var line = await _db.BankStatementLines
                .FirstOrDefaultAsync(l => l.StatementLineId == body.StatementLineId && l.ReconciliationId == id);
            if (line is null) return BadRequest(new { message = "That statement line is not on this reconciliation." });

            if (body.JournalEntryLineId is int ledgerLineId)
            {
                var ledgerLine = await _db.JournalEntryLines
                    .Include(l => l.Entry).ThenInclude(e => e.Status)
                    .FirstOrDefaultAsync(l => l.LineId == ledgerLineId);

                if (ledgerLine is null)
                    return BadRequest(new { message = "That ledger line does not exist." });
                if (ledgerLine.AccountId != recon.AccountId)
                    return BadRequest(new { message = "That ledger line belongs to a different account." });
                if (ledgerLine.Entry.Status.StatusKey != Posted)
                    return BadRequest(new { message = "Only a posted entry can be reconciled." });

                var taken = await _db.BankStatementLines.AnyAsync(l =>
                    l.MatchedLineId == ledgerLineId && l.StatementLineId != line.StatementLineId);
                if (taken)
                    return BadRequest(new { message = "That ledger line is already matched to another statement line." });

                line.MatchedLineId = ledgerLineId;
            }
            else
            {
                line.MatchedLineId = null;
            }

            await _db.SaveChangesAsync();

            var remaining = await _db.BankStatementLines
                .CountAsync(l => l.ReconciliationId == id && l.MatchedLineId == null);

            return Ok(new
            {
                id,
                statementLineId = line.StatementLineId,
                matchedLineId = line.MatchedLineId,
                unmatchedCount = remaining,
                message = line.MatchedLineId is null ? "Match removed." : "Lines matched."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"match a line on reconciliation {id}");
        }
    }

    /// <summary>
    /// Finalises a reconciliation. Refuses while any statement line is still
    /// unmatched, and refuses if the matched movement does not carry the opening
    /// balance to the closing balance -- signing off a statement that does not
    /// add up is the one thing this screen exists to prevent.
    /// </summary>
    [HttpPost("reconciliation/{id:int}/finalize")]
    public async Task<IActionResult> FinalizeReconciliation(int id)
    {
        try
        {
            var recon = await _db.BankReconciliations
                .Include(r => r.Status)
                .Include(r => r.BankStatementLines)
                .FirstOrDefaultAsync(r => r.ReconciliationId == id);
            if (recon is null) return NotFound(new { message = $"No reconciliation with id {id}." });

            if (recon.FinalizedOn is not null)
                return BadRequest(new { message = "This reconciliation is already finalised." });

            var unmatched = recon.BankStatementLines.Count(l => l.MatchedLineId == null);
            if (unmatched > 0)
                return BadRequest(new
                {
                    message = $"{unmatched} statement {(unmatched == 1 ? "line is" : "lines are")} " +
                              $"still unmatched. Match or explain them before finalising."
                });

            var movement = recon.BankStatementLines.Sum(l => l.Amount);
            var expected = recon.ClosingBalance - recon.OpeningBalance;
            if (Math.Abs(movement - expected) > 0.01m)
                return BadRequest(new
                {
                    message = $"The statement lines move {movement:N2} but the balances move {expected:N2}. " +
                              $"A difference of {Math.Abs(movement - expected):N2} is unexplained."
                });

            var reconciled = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "RECONCILED");
            if (reconciled is null)
                return BadRequest(new { message = "No RECONCILED status is configured." });

            recon.StatusId = reconciled.StatusId;
            recon.FinalizedOn = Today();
            await _db.SaveChangesAsync();
            await Log("RECONCILIATION_FINALIZED", "BankReconciliation", recon.ReconciliationId.ToString(),
                      $"Statement to {recon.StatementDate:yyyy-MM-dd} signed off", 2);

            return Ok(new { id, message = "Reconciliation finalised." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"finalise reconciliation {id}");
        }
    }

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                accounts = await _db.Accounts.AsNoTracking()
                    .Where(a => a.IsActive)
                    .OrderBy(a => a.AccountCode)
                    .Select(a => new
                    {
                        id = a.AccountId, code = a.AccountCode, name = a.AccountName,
                        isGroup = a.IsGroup, type = a.AccountType.TypeName,
                        group = a.AccountType.Group.GroupName
                    })
                    .ToListAsync(),
                entryTypes = await _db.JournalEntryTypes.AsNoTracking()
                    .Select(t => new { id = t.EntryTypeId, key = t.TypeKey, name = t.TypeName })
                    .ToListAsync(),
                voucherTypes = await _db.VoucherTypes.AsNoTracking()
                    .Select(t => new { id = t.VoucherTypeId, code = t.TypeCode, name = t.TypeName, isReceipt = t.IsReceipt })
                    .ToListAsync(),
                postingStatuses = await _db.PostingStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                collectionStatuses = await _db.CollectionStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                paymentMethods = await _db.PaymentMethods.AsNoTracking()
                    .Where(m => m.IsActive)
                    .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName, kind = m.MethodKind })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                periods = await _db.FiscalPeriods.AsNoTracking()
                    .OrderByDescending(p => p.StartDate)
                    .Select(p => new { id = p.PeriodId, name = p.PeriodName, isClosed = p.IsClosed })
                    .ToListAsync(),
                /* type comes from the USER's role, the same way PartiesController
                   reads it -- 5 customer, 6 supplier, 7 both. A receipt voucher
                   should not offer suppliers and a payment should not offer
                   customers, and without this the screen cannot tell them apart. */
                parties = await _db.Parties.AsNoTracking()
                    .Where(p => p.User.IsActive).OrderBy(p => p.LegalName)
                    .Select(p => new
                    {
                        id = p.UserId,
                        code = p.PartyCode,
                        name = p.LegalName,
                        type = p.User.RoleId == 6 ? "SUPPLIER"
                             : p.User.RoleId == 7 ? "BOTH" : "CUSTOMER"
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load accounting lookups");
        }
    }


    // ══════════════════════════ request bodies ══════════════════════════

    public record JournalLineRequest(
        int AccountId, int? PartyId, string? Description, decimal Debit, decimal Credit);

    public record JournalEntryRequest(
        DateOnly? EntryDate, int LocationId, string? Reference, string? Narration,
        bool PostImmediately, List<JournalLineRequest> Lines);

    public record ExpenseRequest(
        DateOnly? ExpenseDate, int LocationId, string? CategoryName,
        int ExpenseAccountId, int PaidFromAccountId, decimal Amount,
        string VendorName, int MethodId, string? Description);

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  voucher
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Records money in or out. The voucher TYPE decides the direction
    /// (VoucherType.IsReceipt), and allocations attach it to the invoices it
    /// pays -- an unallocated voucher is money that arrived with no idea what
    /// it settles, which is how a customer ledger stops agreeing with the
    /// invoice list.
    /// </summary>
    /// <summary>
    /// The unpaid invoices a voucher can be allocated against.
    ///
    /// Neither SalesInvoice nor PurchaseInvoice carries a "paid" column, so
    /// what has been settled is the sum of the allocations pointed at it. Only
    /// POSTED vouchers count -- a draft is somebody's intention, not a payment,
    /// and treating it as one would hide money that is still owed.
    ///
    /// kind: "sales" (money coming in) or "purchase" (money going out).
    /// </summary>
    [HttpGet("open-invoices")]
    public async Task<IActionResult> GetOpenInvoices(
        [FromQuery] string kind = "sales", [FromQuery] int? partyId = null,
        [FromQuery] string? q = null, [FromQuery] int take = 50)
    {
        try
        {
            if (take is < 1 or > 200) take = 50;
            var wanted = (kind ?? "sales").Trim().ToLowerInvariant();
            if (wanted is not ("sales" or "purchase"))
                return BadRequest(new { message = $"'{kind}' is not an invoice kind. Use sales or purchase." });

            if (wanted == "sales")
            {
                var rows = _db.SalesInvoices.AsNoTracking()
                    .Where(i => i.Status.StatusKey != "CANCELLED");
                if (partyId is not null) rows = rows.Where(i => i.CustomerUserId == partyId);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim().ToLower();
                    rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term));
                }

                var list = await rows
                    .Select(i => new
                    {
                        id = i.InvoiceId,
                        invoiceNo = i.InvoiceNo,
                        invoiceDate = i.InvoiceDate,
                        dueDate = i.DueDate,
                        partyId = i.CustomerUserId,
                        partyName = i.CustomerUser.LegalName,
                        total = i.TotalAmount,
                        paid = i.VoucherAllocations
                            .Where(a => a.Voucher.Status.StatusKey == Posted)
                            .Sum(a => (decimal?)a.Amount) ?? 0m
                    })
                    .ToListAsync();

                var open = list.Select(i => new
                    {
                        i.id, i.invoiceNo, i.invoiceDate, i.dueDate, i.partyId, i.partyName,
                        i.total, i.paid, balance = i.total - i.paid
                    })
                    .Where(i => i.balance > 0.004m)
                    .OrderBy(i => i.dueDate).ThenBy(i => i.invoiceNo)
                    .Take(take).ToList();

                return Ok(new { kind = wanted, count = open.Count, outstanding = open.Sum(i => i.balance), items = open });
            }
            else
            {
                var rows = _db.PurchaseInvoices.AsNoTracking()
                    .Where(i => i.Status.StatusKey != "CANCELLED");
                if (partyId is not null) rows = rows.Where(i => i.SupplierUserId == partyId);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim().ToLower();
                    rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term) ||
                                           i.SupplierInvoiceNo.ToLower().Contains(term));
                }

                var list = await rows
                    .Select(i => new
                    {
                        id = i.PiId,
                        invoiceNo = i.InvoiceNo,
                        supplierInvoiceNo = i.SupplierInvoiceNo,
                        invoiceDate = i.InvoiceDate,
                        dueDate = i.DueDate,
                        partyId = i.SupplierUserId,
                        partyName = i.SupplierUser.LegalName,
                        total = i.TotalAmount,
                        paid = i.VoucherAllocations
                            .Where(a => a.Voucher.Status.StatusKey == Posted)
                            .Sum(a => (decimal?)a.Amount) ?? 0m
                    })
                    .ToListAsync();

                var open = list.Select(i => new
                    {
                        i.id, i.invoiceNo, i.supplierInvoiceNo, i.invoiceDate, i.dueDate,
                        i.partyId, i.partyName, i.total, i.paid, balance = i.total - i.paid
                    })
                    .Where(i => i.balance > 0.004m)
                    .OrderBy(i => i.dueDate).ThenBy(i => i.invoiceNo)
                    .Take(take).ToList();

                return Ok(new { kind = wanted, count = open.Count, outstanding = open.Sum(i => i.balance), items = open });
            }
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the open invoices");
        }
    }

    [HttpPost("vouchers")]
    public async Task<IActionResult> CreateVoucher([FromBody] VoucherRequest body)
    {
        try
        {
            if (body.Amount <= 0)
                return BadRequest(new { message = "A voucher needs an amount above zero." });

            var type = await _db.VoucherTypes.FirstOrDefaultAsync(t => t.VoucherTypeId == body.VoucherTypeId);
            if (type is null) return BadRequest(new { message = "Pick a valid voucher type." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.MethodId))
                return BadRequest(new { message = "Pick a valid payment method." });
            if (body.PartyId is not null && !await _db.Parties.AnyAsync(p => p.UserId == body.PartyId))
                return BadRequest(new { message = "Pick a valid party." });
            if (body.CashBankAccountId is not null &&
                !await _db.Accounts.AnyAsync(a => a.AccountId == body.CashBankAccountId && !a.IsGroup))
                return BadRequest(new { message = "Pick a valid cash or bank account." });

            var allocated = (body.Allocations ?? new List<VoucherAllocationRequest>()).Sum(a => a.Amount);
            if (allocated > body.Amount)
                return BadRequest(new
                {
                    message = $"Allocated {allocated:N2} is more than the voucher amount {body.Amount:N2}."
                });

            var draft = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            if (draft is null || posted is null)
                return BadRequest(new { message = "Posting statuses are not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var v = new Voucher
            {
                VoucherNo = await NextNumber(type.IsReceipt ? "RV" : "PV"),
                VoucherTypeId = type.VoucherTypeId,
                VoucherDate = body.VoucherDate ?? Today(),
                LocationId = body.LocationId,
                PartyUserId = body.PartyId,
                CashBankAccountId = body.CashBankAccountId,
                Amount = body.Amount,
                MethodId = body.MethodId,
                PaymentProvider = body.PaymentProvider,
                ReferenceNo = body.Reference,
                WalletTxnId = body.WalletTxnId,
                Narration = body.Narration ?? "",
                StatusId = body.PostImmediately ? posted.StatusId : draft.StatusId,
                CreatedByUserId = CurrentUserId()
            };
            _db.Vouchers.Add(v);
            await _db.SaveChangesAsync();

            foreach (var a in body.Allocations ?? new List<VoucherAllocationRequest>())
            {
                if (a.Amount <= 0)
                    return BadRequest(new { message = "An allocation cannot be zero or negative." });
                if (a.SalesInvoiceId is null && a.PurchaseInvoiceId is null)
                    return BadRequest(new { message = "An allocation must point at a sale or purchase invoice." });

                _db.VoucherAllocations.Add(new VoucherAllocation
                {
                    VoucherId = v.VoucherId,
                    SalesInvoiceId = a.SalesInvoiceId,
                    PurchaseInvoiceId = a.PurchaseInvoiceId,
                    Amount = a.Amount
                });
            }
            await _db.SaveChangesAsync();

            /* Saved AND posted in one go still has to reach the ledger. This is
               the same call PostVoucher makes -- one path, so a voucher posted
               on creation and a voucher posted later produce the same entry. */
            if (body.PostImmediately)
            {
                var why = await PostVoucherToLedger(v);
                if (why is not null) return BadRequest(new { message = why });
                await _db.SaveChangesAsync();
            }

            await tx.CommitAsync();

            await Log(type.IsReceipt ? "RECEIPT_RECORDED" : "PAYMENT_RECORDED",
                "Voucher", v.VoucherNo, $"{body.Amount:N2}", 2);

            /* The PDF exists the moment the document does. Print and Download
               then hand out the stored Cloudinary file rather than rendering a
               fresh one, so what is on screen is what is in the store. A
               failure here is logged and swallowed -- the document is saved
               either way and the PDF can be rebuilt from the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "voucher", v.VoucherId, CurrentUserId());

            return Ok(new
            {
                id = v.VoucherId,
                voucherNo = v.VoucherNo,
                unallocated = body.Amount - allocated,
                message = $"{v.VoucherNo} saved" + (body.PostImmediately ? " and posted." : " as a draft.")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the voucher");
        }
    }

    /// <summary>Posts a draft voucher.</summary>
    [HttpPost("vouchers/{id:int}/post")]
    public async Task<IActionResult> PostVoucher(int id)
    {
        try
        {
            var v = await _db.Vouchers.Include(x => x.Status)
                .FirstOrDefaultAsync(x => x.VoucherId == id);
            if (v is null) return NotFound(new { message = $"No voucher with id {id}." });
            if (v.Status.StatusKey == Posted)
                return BadRequest(new { message = $"{v.VoucherNo} is already posted." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            if (posted is null) return BadRequest(new { message = "No POSTED status is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            /* Posting is the moment the money is recognised, so it is also the
               moment the ledger has to hear about it. Before this the status
               flipped on its own and EntryId stayed null -- a posted voucher
               that no statement in the app could see, and a customer invoice
               that never cleared. */
            if (v.EntryId is null)
            {
                var why = await PostVoucherToLedger(v);
                if (why is not null) return BadRequest(new { message = why });
            }

            v.StatusId = posted.StatusId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Log("VOUCHER_POSTED", "Voucher", v.VoucherNo, $"{v.Amount:N2}", 2);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "voucher", v.VoucherId, CurrentUserId());

            var entryNo = await _db.JournalEntries.AsNoTracking()
                .Where(e => e.EntryId == v.EntryId).Select(e => e.EntryNo).FirstOrDefaultAsync();

            /* -- C6 -- the rep who looks after this customer is told, because
               "have they paid yet" is a question they get asked. */
            var partyName = v.PartyUserId is null ? null : await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == v.PartyUserId).Select(pa => pa.LegalName).FirstOrDefaultAsync();
            var repId = v.PartyUserId is null ? null : await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == v.PartyUserId).Select(pa => pa.SalesPersonUserId).FirstOrDefaultAsync();

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.VoucherPosted,
                $"Payment posted by {CurrentUserName()}",
                $"{v.VoucherNo} -- PKR {v.Amount:N0}" +
                (partyName is null ? "." : $" with {partyName}."),
                url: $"/accounting/vouchers/{v.VoucherId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: repId is null ? null : new[] { repId.Value });

            return Ok(new { id, entryNo, message = $"{v.VoucherNo} posted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"post voucher {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  EXPORTS
    // ══════════════════════════════════════════════════════════════════

    /*  Each export runs the SAME list action the screen runs and writes its
        result, so the workbook is what was on the page -- filters, search and
        all. Re-running a copy of the query here is how an export quietly starts
        disagreeing with the screen it was taken from.

        pageSize 5000: an export is the one place a full list is wanted, and the
        screen itself stays paginated.                                        */

    [HttpGet("expenses/export")]
    public async Task<IActionResult> ExportExpenses(
        [FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var action = await GetExpenses(q, status, from, to, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Expense No", "expenseNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Date", "expenseDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Category", "categoryName", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Vendor", "vendorName", XlsxWriter.CellKind.Text, 28),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Expense Account", "expenseAccount", XlsxWriter.CellKind.Text, 26),
                new XlsxWriter.Column("Paid From", "paidFromAccount", XlsxWriter.CellKind.Text, 22),
                new XlsxWriter.Column("Paid Via", "paymentMethod"),
                new XlsxWriter.Column("Amount", "amount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Entry No", "entryNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Recorded By", "createdBy", XlsxWriter.CellKind.Text, 22),
                new XlsxWriter.Column("Description", "description", XlsxWriter.CellKind.Text, 40),
            };

            var bytes = XlsxWriter.FromPayload("Expenses",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"expenses-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the expenses");
        }
    }

    [HttpGet("journal-entries/export")]
    public async Task<IActionResult> ExportJournalEntries(
        [FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var action = await GetJournalEntries(q, status, from, to, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Entry No", "entryNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Date", "entryDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Type", "entryTypeName", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Narration", "narration", XlsxWriter.CellKind.Text, 42),
                new XlsxWriter.Column("Debit", "totalDebit", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Credit", "totalCredit", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Reversed By", "reversedBy", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Created By", "createdBy", XlsxWriter.CellKind.Text, 22),
                new XlsxWriter.Column("Posted By", "postedBy", XlsxWriter.CellKind.Text, 22),
            };

            var bytes = XlsxWriter.FromPayload("Journal Entries",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"journal-entries-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the journal entries");
        }
    }

    [HttpGet("vouchers/export")]
    public async Task<IActionResult> ExportVouchers(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? type,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var action = await GetVouchers(q, status, type, from, to, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Voucher No", "voucherNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Date", "date", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Type", "typeName", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Party", "partyName", XlsxWriter.CellKind.Text, 28),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Cash/Bank", "cashBankAccount", XlsxWriter.CellKind.Text, 24),
                new XlsxWriter.Column("Method", "paymentMethod"),
                new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Amount", "amount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Narration", "narration", XlsxWriter.CellKind.Text, 40),
                new XlsxWriter.Column("Created By", "createdBy", XlsxWriter.CellKind.Text, 22),
            };

            var bytes = XlsxWriter.FromPayload("Vouchers",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"vouchers-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the vouchers");
        }
    }

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    // ══════════════════════ request bodies (part 2) ═════════════════════

    public record VoucherAllocationRequest(int? SalesInvoiceId, int? PurchaseInvoiceId, decimal Amount);

    public record MatchLineRequest(int StatementLineId, int? JournalEntryLineId);

    public record VoucherRequest(
        int VoucherTypeId, DateOnly? VoucherDate, int LocationId, int? PartyId,
        int? CashBankAccountId, decimal Amount, int MethodId,
        string? PaymentProvider, string? Reference, string? WalletTxnId,
        string? Narration, bool PostImmediately,
        List<VoucherAllocationRequest>? Allocations);

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENT PDFs
    // ══════════════════════════════════════════════════════════════════

    /*  The five statements -- trial balance, balance sheet, profit and loss,
        cash flow and a ledger -- render to PDF and push to the "CloudinaryPdfs"
        account, exactly like the bills and the reports.

        Until now the only way to get any of them onto paper was the browser's
        own print dialog, which prints the sidebar and the buttons along with
        the numbers, and stores nothing anywhere.

        Each renderer calls the SAME action the browser calls and reads its
        result rather than re-running a copy of the query, so the statement on
        paper and the statement on screen cannot disagree. A statement that
        quietly differs from the screen it was printed from is the worst thing
        an accounting system can produce.                                      */

    private static readonly string[] StatementKeys =
    {
        "trial-balance", "balance-sheet", "profit-loss", "cash-flow", "ledger"
    };

    /// <summary>The statement as a PDF, built on request.</summary>
    [HttpGet("{key}/pdf")]
    public async Task<IActionResult> RenderStatementPdf(string key,
        [FromQuery] DateOnly? asOf, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int accountId = 0)
    {
        try
        {
            var built = await BuildStatement(key, asOf, from, to, accountId);
            if (built.Error is not null) return built.Error;

            Response.Headers.ContentDisposition = $"inline; filename=\"{built.FileName}\"";
            return File(DocumentPdf.Render(built.Doc!), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the {key.Replace('-', ' ')} statement");
        }
    }

    /// <summary>Renders the statement and pushes it to the documents Cloudinary account.</summary>
    [HttpPost("{key}/pdf")]
    public async Task<IActionResult> ArchiveStatementPdf(string key,
        [FromQuery] DateOnly? asOf, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int accountId = 0)
    {
        try
        {
            var built = await BuildStatement(key, asOf, from, to, accountId);
            if (built.Error is not null) return built.Error;

            var kind = $"statement.{key}";
            var stored = await DocumentArchive.StoreAsync(_db, _cfg, kind, built.Fingerprint!,
                built.Doc!.Title, built.FileName!, DocumentPdf.Render(built.Doc!),
                CurrentUserId(), "statements");

            await Log("STATEMENT_ARCHIVED", kind, built.Doc!.Title, stored.PdfUrl, 1);

            return Ok(new
            {
                archived = true,
                fileId = stored.FileId,
                kind,
                fileName = stored.FileName,
                pdfUrl = stored.PdfUrl,
                bytes = stored.Bytes,
                isDeliverable = stored.Deliverable,
                generatedAt = stored.GeneratedAt,
                message = stored.Deliverable
                    ? $"{built.Doc!.Title} saved to the document store."
                    : $"{built.Doc!.Title} saved. The store will not serve PDFs yet -- see the Cloudinary setting."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the {key.Replace('-', ' ')} statement");
        }
    }

    private sealed record BuiltStatement(
        DocumentPdf.Data? Doc, string? FileName, string? Fingerprint, IActionResult? Error);

    private async Task<BuiltStatement> BuildStatement(string key,
        DateOnly? asOf, DateOnly? from, DateOnly? to, int accountId)
    {
        if (!StatementKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new BuiltStatement(null, null, null,
                NotFound(new { message = $"'{key}' is not a statement. Try: {string.Join(", ", StatementKeys)}." }));

        if (key.Equals("ledger", StringComparison.OrdinalIgnoreCase) && accountId <= 0)
            return new BuiltStatement(null, null, null,
                BadRequest(new { message = "A ledger needs an accountId." }));

        IActionResult action = key.ToLowerInvariant() switch
        {
            "trial-balance" => await GetTrialBalance(asOf),
            "balance-sheet" => await GetBalanceSheet(asOf),
            "profit-loss" => await GetProfitLoss(from, to),
            "cash-flow" => await GetCashFlow(from, to),
            _ => await GetLedger(accountId, from, to)
        };

        if (action is not OkObjectResult ok || ok.Value is null)
            return new BuiltStatement(null, null, null, action);

        var j = JsonSerializer.SerializeToElement(ok.Value, PdfJson);
        var c = await PdfLetterHead();
        var cur = c.CurrencySymbol;

        return key.ToLowerInvariant() switch
        {
            "trial-balance" => new BuiltStatement(TrialBalancePdf(j, c, cur),
                $"trial-balance-{PdfStr(j, "asOf")}.pdf", PdfStr(j, "asOf"), null),

            "balance-sheet" => new BuiltStatement(BalanceSheetPdf(j, c, cur),
                $"balance-sheet-{PdfStr(j, "asOf")}.pdf", PdfStr(j, "asOf"), null),

            "profit-loss" => new BuiltStatement(ProfitLossPdf(j, c, cur),
                $"profit-and-loss-{PdfStr(j, "from")}-to-{PdfStr(j, "to")}.pdf",
                $"{PdfStr(j, "from")}:{PdfStr(j, "to")}", null),

            "cash-flow" => new BuiltStatement(CashFlowPdf(j, c, cur),
                $"cash-flow-{PdfStr(j, "from")}-to-{PdfStr(j, "to")}.pdf",
                $"{PdfStr(j, "from")}:{PdfStr(j, "to")}", null),

            _ => new BuiltStatement(LedgerPdf(j, c, cur),
                $"ledger-{PdfStr(j.GetProperty("account"), "code")}.pdf",
                $"{accountId}:{from}:{to}", null)
        };
    }

    /* ─────────────────────── the five layouts ─────────────────────── */

    private DocumentPdf.Data TrialBalancePdf(JsonElement j, DocumentPdf.LetterHead c, string cur)
    {
        var balanced = j.TryGetProperty("isBalanced", out var b) && b.ValueKind == JsonValueKind.True;
        var note = PdfStr(j, "note");

        return new DocumentPdf.Data(
            Company: c,
            Title: "Trial Balance",
            DocNo: null,
            StatusName: balanced ? "Balanced" : "Out of balance",
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("As At", PdfDay(j, "asOf")),
                new DocumentPdf.Fact("Accounts", PdfArr(j, "lines").Count().ToString()),
                new DocumentPdf.Fact("Posted Debit", DocumentPdf.Money(PdfDec(j, "movementDebit"))),
                new DocumentPdf.Fact("Posted Credit", DocumentPdf.Money(PdfDec(j, "movementCredit"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Code", 1.5),
                new DocumentPdf.Col("Account", 5),
                new DocumentPdf.Col("Group", 2.2),
                new DocumentPdf.Col("Debit", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Credit", 2, DocumentPdf.Align.Right),
            },
            Rows: PdfArr(j, "lines").Select(l => new DocumentPdf.Row(new[]
            {
                PdfStr(l, "code"), PdfStr(l, "name"), PdfStr(l, "group"),
                PdfZero(PdfDec(l, "debitBalance")), PdfZero(PdfDec(l, "creditBalance"))
            })).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Total debit", DocumentPdf.Money(PdfDec(j, "totalDebit"), cur)),
                new DocumentPdf.Total("Total credit", DocumentPdf.Money(PdfDec(j, "totalCredit"), cur)),
                new DocumentPdf.Total(balanced ? "Balanced" : "Difference",
                    DocumentPdf.Money(Math.Abs(PdfDec(j, "totalDebit") - PdfDec(j, "totalCredit")), cur),
                    Emphasis: true),
            },
            Notes: string.IsNullOrWhiteSpace(note) ? null : note,
            Footnote: "Posted entries only. Opening balances are carried on the account's natural side.",
            PreparedBy: null,
            EmptyMessage: "No posted movement as at this date.");
    }

    private DocumentPdf.Data BalanceSheetPdf(JsonElement j, DocumentPdf.LetterHead c, string cur)
    {
        DocumentPdf.Col[] Cols() => new[]
        {
            new DocumentPdf.Col("Code", 1.6),
            new DocumentPdf.Col("Account", 6.4),
            new DocumentPdf.Col("Balance", 2.4, DocumentPdf.Align.Right),
        };

        List<DocumentPdf.Row> Group(string name) => PdfArr(j, name).Select(a => new DocumentPdf.Row(new[]
        {
            PdfStr(a, "code"), PdfStr(a, "name"), DocumentPdf.Money(PdfDec(a, "balance"))
        }, Sub: PdfStr(a, "type"))).ToList();

        var diff = PdfDec(j, "difference");

        return new DocumentPdf.Data(
            Company: c,
            Title: "Balance Sheet",
            DocNo: null,
            StatusName: diff == 0 ? "Balanced" : "Difference reported",
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("As At", PdfDay(j, "asOf")),
                new DocumentPdf.Fact("Assets", DocumentPdf.Money(PdfDec(j, "totalAssets"))),
                new DocumentPdf.Fact("Liabilities", DocumentPdf.Money(PdfDec(j, "totalLiabilities"))),
                new DocumentPdf.Fact("Equity", DocumentPdf.Money(PdfDec(j, "totalEquity"))),
            },
            Columns: Cols(),
            Rows: Group("assets"),
            Totals: new[]
            {
                new DocumentPdf.Total("Total assets", DocumentPdf.Money(PdfDec(j, "totalAssets"), cur)),
                new DocumentPdf.Total("Total liabilities", DocumentPdf.Money(PdfDec(j, "totalLiabilities"), cur)),
                new DocumentPdf.Total("Total equity", DocumentPdf.Money(PdfDec(j, "totalEquity"), cur)),
                new DocumentPdf.Total(diff == 0 ? "Balanced" : "Unexplained Difference",
                    DocumentPdf.Money(diff, cur), Emphasis: true),
            },
            Notes: diff == 0 ? null
                : "Retained earnings are not a stored account in this chart, so the gap between the "
                + "two sides is reported rather than hidden. A balance sheet that silently balances "
                + "is worse than one that shows you the difference.",
            Footnote: "Posted entries only, from the opening balances forward.",
            PreparedBy: null,
            EmptyMessage: "No asset balances as at this date.",
            More: new[]
            {
                new DocumentPdf.Section("Liabilities", Cols(), Group("liabilities")),
                new DocumentPdf.Section("Equity", Cols(), Group("equity")),
            });
    }

    private DocumentPdf.Data ProfitLossPdf(JsonElement j, DocumentPdf.LetterHead c, string cur)
    {
        DocumentPdf.Col[] Cols() => new[]
        {
            new DocumentPdf.Col("Code", 1.6),
            new DocumentPdf.Col("Account", 6.4),
            new DocumentPdf.Col("Amount", 2.4, DocumentPdf.Align.Right),
        };

        List<DocumentPdf.Row> Group(string name) => PdfArr(j, name).Select(a => new DocumentPdf.Row(new[]
        {
            PdfStr(a, "code"), PdfStr(a, "name"), DocumentPdf.Money(PdfDec(a, "amount"))
        }, Sub: PdfStr(a, "type"))).ToList();

        var net = PdfDec(j, "netProfit");

        return new DocumentPdf.Data(
            Company: c,
            Title: "Profit and Loss",
            DocNo: null,
            StatusName: net >= 0 ? "Profit" : "Loss",
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", PdfDay(j, "from")),
                new DocumentPdf.Fact("To", PdfDay(j, "to")),
                new DocumentPdf.Fact("Income", DocumentPdf.Money(PdfDec(j, "totalIncome"))),
                new DocumentPdf.Fact("Expense", DocumentPdf.Money(PdfDec(j, "totalExpense"))),
            },
            Columns: Cols(),
            Rows: Group("income"),
            Totals: new[]
            {
                new DocumentPdf.Total("Total income", DocumentPdf.Money(PdfDec(j, "totalIncome"), cur)),
                new DocumentPdf.Total("Total expense", DocumentPdf.Money(PdfDec(j, "totalExpense"), cur),
                    Colour: DocumentPdf.Danger),
                new DocumentPdf.Total(net >= 0 ? "Net Profit" : "Net Loss",
                    DocumentPdf.Money(Math.Abs(net), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Posted entries only, for the period shown. Income is credit-net, expense is debit-net.",
            PreparedBy: null,
            EmptyMessage: "No income posted in this period.",
            More: new[] { new DocumentPdf.Section("Expenses", Cols(), Group("expense")) });
    }

    private DocumentPdf.Data CashFlowPdf(JsonElement j, DocumentPdf.LetterHead c, string cur)
    {
        var net = PdfDec(j, "netChange");

        return new DocumentPdf.Data(
            Company: c,
            Title: "Cash Flow",
            DocNo: null,
            StatusName: net >= 0 ? "Net inflow" : "Net outflow",
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", PdfDay(j, "from")),
                new DocumentPdf.Fact("To", PdfDay(j, "to")),
                new DocumentPdf.Fact("In", DocumentPdf.Money(PdfDec(j, "totalInflow"))),
                new DocumentPdf.Fact("Out", DocumentPdf.Money(PdfDec(j, "totalOutflow"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Code", 1.6),
                new DocumentPdf.Col("Cash / Bank Account", 4.6),
                new DocumentPdf.Col("In", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Out", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Net", 2, DocumentPdf.Align.Right),
            },
            Rows: PdfArr(j, "lines").Select(l => new DocumentPdf.Row(new[]
            {
                PdfStr(l, "code"), PdfStr(l, "name"),
                PdfZero(PdfDec(l, "inflow")), PdfZero(PdfDec(l, "outflow")),
                DocumentPdf.Money(PdfDec(l, "net"))
            })).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Total in", DocumentPdf.Money(PdfDec(j, "totalInflow"), cur),
                    Colour: DocumentPdf.Success),
                new DocumentPdf.Total("Total out", DocumentPdf.Money(PdfDec(j, "totalOutflow"), cur),
                    Colour: DocumentPdf.Danger),
                new DocumentPdf.Total("Net Change", DocumentPdf.Money(net, cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Movement across cash and bank accounts from posted entries only.",
            PreparedBy: null,
            EmptyMessage: "No cash or bank movement in this period.");
    }

    private DocumentPdf.Data LedgerPdf(JsonElement j, DocumentPdf.LetterHead c, string cur)
    {
        var acc = j.GetProperty("account");

        return new DocumentPdf.Data(
            Company: c,
            Title: "Account Ledger",
            DocNo: PdfStr(acc, "code"),
            StatusName: null,
            Counterparty: new DocumentPdf.Party("Account", PdfStr(acc, "name"),
                new[] { PdfStr(acc, "code"), PdfStr(acc, "type") }),
            Meta: new[]
            {
                new DocumentPdf.Fact("Opening", DocumentPdf.Money(PdfDec(j, "openingBalance"))),
                new DocumentPdf.Fact("Closing", DocumentPdf.Money(PdfDec(j, "closingBalance"))),
                new DocumentPdf.Fact("Entries", PdfArr(j, "lines").Count().ToString()),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Date", 1.6),
                new DocumentPdf.Col("Entry", 1.6),
                new DocumentPdf.Col("Narration", 3.4),
                new DocumentPdf.Col("Debit", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Credit", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Balance", 1.8, DocumentPdf.Align.Right),
            },
            Rows: PdfArr(j, "lines").Select(l => new DocumentPdf.Row(new[]
            {
                PdfDay(l, "date"), PdfStr(l, "entryNo"),
                PdfStr(l, "narration"),
                PdfZero(PdfDec(l, "debit")), PdfZero(PdfDec(l, "credit")),
                DocumentPdf.Money(PdfDec(l, "balance"))
            }, Sub: PdfStr(l, "party"))).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Opening balance", DocumentPdf.Money(PdfDec(j, "openingBalance"), cur)),
                new DocumentPdf.Total("Total debit", DocumentPdf.Money(PdfDec(j, "totalDebit"), cur)),
                new DocumentPdf.Total("Total credit", DocumentPdf.Money(PdfDec(j, "totalCredit"), cur)),
                new DocumentPdf.Total("Closing Balance", DocumentPdf.Money(PdfDec(j, "closingBalance"), cur),
                    Emphasis: true),
            },
            Notes: null,
            Footnote: "Posted entries only, in date order. The running balance is on a debit basis.",
            PreparedBy: null,
            EmptyMessage: "No posted movement on this account for the period.");
    }

    /* ───────────────────── json readers + letterhead ───────────────────── */

    private static readonly JsonSerializerOptions PdfJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    private static IEnumerable<JsonElement> PdfArr(JsonElement j, string name) =>
        j.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string PdfStr(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static decimal PdfDec(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static string PdfDay(JsonElement j, string name) =>
        DateOnly.TryParse(PdfStr(j, name), CultureInfo.InvariantCulture, out var d)
            ? DocumentPdf.Day(d)
            : PdfStr(j, name);

    /// <summary>A dash reads better than a column of 0.00 down a statement.</summary>
    private static string PdfZero(decimal v) => v == 0 ? "-" : DocumentPdf.Money(v);

    private async Task<DocumentPdf.LetterHead> PdfLetterHead()
    {
        var c = await _db.Companies.AsNoTracking()
            .Select(x => new
            {
                x.CompanyName, x.LegalName, x.AddressLine,
                city = x.City.CityName,
                x.Country, x.Phone, x.Email, x.Ntn, x.Strn, x.CurrencySymbol
            })
            .FirstOrDefaultAsync();

        return new DocumentPdf.LetterHead(
            c?.CompanyName ?? "AdvPOS",
            c?.LegalName ?? c?.CompanyName ?? "AdvPOS",
            c?.AddressLine ?? "", c?.city ?? "", c?.Country ?? "",
            c?.Phone ?? "", c?.Email ?? "", c?.Ntn ?? "", c?.Strn ?? "",
            c?.CurrencySymbol ?? "PKR");
    }


    // ══════════════════════════════════════════════════════════════════
    //  EDIT, DELETE AND THE STATUS MOVES
    // ══════════════════════════════════════════════════════════════════

    /*  Until now the three accounting documents could only be CREATED and, for
        two of them, posted. There was no way to correct a typo, throw away a
        draft, reverse an entry that had gone in wrong, or reject an expense --
        which meant the only remedy for any mistake was another entry on top of
        it, made by hand, in the database.

        THE RULE THAT GOVERNS ALL OF IT: a DRAFT is scratch, a POSTED document is
        history. Drafts can be edited and deleted freely. Nothing posted is ever
        edited or deleted -- it is reversed or cancelled, and the reversal is
        itself a record. That is not politeness, it is what makes a ledger worth
        reading a year later.                                                   */

    /// <summary>The draft check every edit and delete below shares.</summary>
    private static string? WhyLocked(string statusKey, string docNo) => statusKey switch
    {
        "DRAFT" => null,
        "POSTED" => $"{docNo} is posted. Reverse it instead -- a posted document is history and is never edited.",
        "REVERSED" => $"{docNo} has already been reversed.",
        "CANCELLED" => $"{docNo} is cancelled.",
        "REJECTED" => $"{docNo} was rejected. Raise a fresh one rather than reopening this.",
        "RECONCILED" => $"{docNo} is reconciled against a bank statement and cannot be changed.",
        _ => $"{docNo} is {statusKey.ToLowerInvariant()} and cannot be changed."
    };

    /* ─────────────────────────── expenses ─────────────────────────── */

    /// <summary>Corrects a draft expense. Everything on the form can change.</summary>
    [HttpPut("expenses/{id:int}")]
    public async Task<IActionResult> UpdateExpense(int id, [FromBody] ExpenseRequest body)
    {
        try
        {
            var e = await _db.Expenses.Include(x => x.Status).FirstOrDefaultAsync(x => x.ExpenseId == id);
            if (e is null) return NotFound(new { message = $"No expense with id {id}." });

            var locked = WhyLocked(e.Status.StatusKey, e.ExpenseNo);
            if (locked is not null) return BadRequest(new { message = locked });

            var invalid = await ValidateExpense(body);
            if (invalid is not null) return BadRequest(new { message = invalid });

            e.ExpenseDate = body.ExpenseDate ?? e.ExpenseDate;
            e.LocationId = body.LocationId;
            e.CategoryName = string.IsNullOrWhiteSpace(body.CategoryName) ? e.CategoryName : body.CategoryName.Trim();
            e.ExpenseAccountId = body.ExpenseAccountId;
            e.PaidFromAccountId = body.PaidFromAccountId;
            e.Amount = body.Amount;
            e.VendorName = body.VendorName.Trim();
            e.MethodId = body.MethodId;
            e.Description = body.Description?.Trim();

            await _db.SaveChangesAsync();
            await Log("EXPENSE_UPDATED", "Expense", e.ExpenseNo, $"{e.Amount:N2} to {e.VendorName}", 2);

            /* The stored PDF is now out of date, so rebuild it rather than leave
               a document in the store that disagrees with the row. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "expense", e.ExpenseId, CurrentUserId());

            return Ok(new { id, expenseNo = e.ExpenseNo, message = $"{e.ExpenseNo} updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update expense {id}");
        }
    }

    /// <summary>Throws away a draft expense.</summary>
    [HttpDelete("expenses/{id:int}")]
    public async Task<IActionResult> DeleteExpense(int id)
    {
        try
        {
            var e = await _db.Expenses.Include(x => x.Status).FirstOrDefaultAsync(x => x.ExpenseId == id);
            if (e is null) return NotFound(new { message = $"No expense with id {id}." });

            var locked = WhyLocked(e.Status.StatusKey, e.ExpenseNo);
            if (locked is not null) return BadRequest(new { message = locked });

            var no = e.ExpenseNo;
            _db.Expenses.Remove(e);
            await _db.SaveChangesAsync();
            await Log("EXPENSE_DELETED", "Expense", no, $"{e.Amount:N2} to {e.VendorName}", 3);

            return Ok(new { id, message = $"{no} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete expense {id}");
        }
    }

    /// <summary>
    /// Approves or rejects a draft expense. Approving posts it; rejecting needs
    /// a reason, because "why was my expense refused" is a question somebody
    /// always asks.
    /// </summary>
    [HttpPatch("expenses/{id:int}/status")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> SetExpenseStatus(int id, [FromBody] DecisionRequest body)
    {
        try
        {
            var key = (body.StatusKey ?? "").Trim().ToUpperInvariant();
            if (key is not ("POSTED" or "REJECTED"))
                return BadRequest(new { message = $"'{body.StatusKey}' is not an expense decision. Use POSTED or REJECTED." });
            if (key == "REJECTED" && string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "Rejecting an expense needs a reason." });

            var e = await _db.Expenses.Include(x => x.Status).FirstOrDefaultAsync(x => x.ExpenseId == id);
            if (e is null) return NotFound(new { message = $"No expense with id {id}." });
            if (e.Status.StatusKey != "DRAFT")
                return BadRequest(new { message = $"{e.ExpenseNo} is already {e.Status.StatusName.ToLowerInvariant()}." });

            var target = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == key);
            if (target is null) return BadRequest(new { message = $"No {key} status is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            /* Approving is the moment the money is recognised, so it is also
               the moment the ledger has to hear about it. Before this the
               status flipped on its own and EntryId stayed null: an approved
               expense that no trial balance, P&L or cash flow could see. */
            if (key == "POSTED" && e.EntryId is null)
            {
                var why = await PostExpenseToLedger(e);
                if (why is not null) return BadRequest(new { message = why });
            }

            e.StatusId = target.StatusId;
            if (!string.IsNullOrWhiteSpace(body.Reason))
                e.Description = string.IsNullOrWhiteSpace(e.Description)
                    ? body.Reason.Trim()
                    : $"{e.Description}\n[{key}] {body.Reason.Trim()}";

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Log($"EXPENSE_{key}", "Expense", e.ExpenseNo,
                body.Reason?.Trim() ?? $"{e.Amount:N2}", key == "REJECTED" ? 3 : 2);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "expense", e.ExpenseId, CurrentUserId());

            /* -- C2 -- the person who filed it is the one waiting on the
               answer, so they are told by name whatever their role. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.ExpenseDecided,
                $"Expense {(key == "POSTED" ? "approved" : "rejected")} by {CurrentUserName()}",
                key == "POSTED"
                    ? $"{e.ExpenseNo} -- PKR {e.Amount:N0} to {e.VendorName}, posted to the ledger."
                    : $"{e.ExpenseNo} was rejected." +
                      (string.IsNullOrWhiteSpace(body.Reason) ? "" : $" Reason: {body.Reason!.Trim()}"),
                url: $"/accounting/expenses/{e.ExpenseId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: new[] { e.CreatedByUserId });

            return Ok(new
            {
                id,
                status = target.StatusKey,
                statusName = target.StatusName,
                message = key == "POSTED" ? $"{e.ExpenseNo} approved." : $"{e.ExpenseNo} rejected."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of expense {id}");
        }
    }

    /// <summary>
    /// The double entry an approved expense makes: the expense account is
    /// debited and the cash or bank account it came out of is credited.
    /// Sets Expense.EntryId. Returns the refusal message, or null on success.
    /// </summary>
    private async Task<string?> PostExpenseToLedger(Expense e)
    {
        var period = await _db.FiscalPeriods
            .FirstOrDefaultAsync(p => p.StartDate <= e.ExpenseDate && p.EndDate >= e.ExpenseDate);
        if (period is null) return $"No fiscal period covers {e.ExpenseDate:yyyy-MM-dd}, so {e.ExpenseNo} cannot be posted.";
        if (period.IsClosed) return $"{period.PeriodName} is closed. {e.ExpenseNo} cannot be posted into it.";

        var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
        if (posted is null) return "No POSTED status is configured.";

        var type = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "EXPENSE")
                   ?? await _db.JournalEntryTypes.FirstAsync();

        var entry = new JournalEntry
        {
            EntryNo = await NextNumber("JV"),
            EntryDate = e.ExpenseDate,
            EntryTypeId = type.EntryTypeId,
            PeriodId = period.PeriodId,
            LocationId = e.LocationId,
            ReferenceNo = e.ExpenseNo,
            Narration = $"{e.CategoryName} -- {e.VendorName}",
            StatusId = posted.StatusId,
            CreatedByUserId = CurrentUserId(),
            PostedByUserId = CurrentUserId(),
            CreatedAt = Today()
        };
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        _db.JournalEntryLines.AddRange(
            new JournalEntryLine
            {
                EntryId = entry.EntryId, LineNo = 1, AccountId = e.ExpenseAccountId,
                Description = string.IsNullOrWhiteSpace(e.Description) ? e.VendorName : e.Description,
                DebitAmount = e.Amount, CreditAmount = 0m
            },
            new JournalEntryLine
            {
                EntryId = entry.EntryId, LineNo = 2, AccountId = e.PaidFromAccountId,
                Description = $"Paid to {e.VendorName}",
                DebitAmount = 0m, CreditAmount = e.Amount
            });
        await _db.SaveChangesAsync();

        e.EntryId = entry.EntryId;
        return null;
    }

    /// <summary>
    /// Undo a posted expense. The original row is never edited -- its journal
    /// entry is reversed by a mirror entry and both are marked REVERSED, so the
    /// history reads "this happened, and then it was undone" rather than "this
    /// never happened".
    /// </summary>
    [HttpPost("expenses/{id:int}/reverse")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> ReverseExpense(int id, [FromBody] ReverseRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Reason))
                return BadRequest(new { message = "Reversing an expense needs a reason." });

            var e = await _db.Expenses.Include(x => x.Status).FirstOrDefaultAsync(x => x.ExpenseId == id);
            if (e is null) return NotFound(new { message = $"No expense with id {id}." });
            if (e.Status.StatusKey != Posted)
                return BadRequest(new { message = $"Only a posted expense can be reversed. {e.ExpenseNo} is {e.Status.StatusName.ToLowerInvariant()}." });

            var reversed = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "REVERSED");
            if (reversed is null) return BadRequest(new { message = "No REVERSED status is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            string? mirrorNo = null;
            if (e.EntryId is not null)
            {
                var original = await _db.JournalEntries
                    .Include(x => x.JournalEntryLines)
                    .FirstAsync(x => x.EntryId == e.EntryId);

                var date = body.ReverseDate ?? Today();
                var period = await _db.FiscalPeriods
                    .FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
                if (period is null) return BadRequest(new { message = $"No fiscal period covers {date:yyyy-MM-dd}." });
                if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. Pick another reversal date." });

                var mirror = new JournalEntry
                {
                    EntryNo = await NextNumber("JV"),
                    EntryDate = date,
                    EntryTypeId = original.EntryTypeId,
                    PeriodId = period.PeriodId,
                    LocationId = original.LocationId,
                    ReferenceNo = original.EntryNo,
                    Narration = $"Reversal of {original.EntryNo} ({e.ExpenseNo}) -- {body.Reason!.Trim()}",
                    StatusId = original.StatusId,
                    CreatedByUserId = CurrentUserId(),
                    PostedByUserId = CurrentUserId(),
                    CreatedAt = Today()
                };
                _db.JournalEntries.Add(mirror);
                await _db.SaveChangesAsync();

                short n = 1;
                foreach (var l in original.JournalEntryLines.OrderBy(l => l.LineNo))
                    _db.JournalEntryLines.Add(new JournalEntryLine
                    {
                        EntryId = mirror.EntryId, LineNo = n++, AccountId = l.AccountId,
                        PartyUserId = l.PartyUserId, Description = l.Description,
                        DebitAmount = l.CreditAmount,      // the swap IS the reversal
                        CreditAmount = l.DebitAmount
                    });

                /* Same rule as ReverseJournalEntry: the original entry keeps
                   POSTED so the two cancel in every statement. The EXPENSE is
                   what becomes REVERSED -- that is a document status, and no
                   statement reads it. */
                original.ReversedByEntryId = mirror.EntryId;
                mirrorNo = mirror.EntryNo;
            }

            e.StatusId = reversed.StatusId;
            e.Description = string.IsNullOrWhiteSpace(e.Description)
                ? $"[REVERSED] {body.Reason!.Trim()}"
                : $"{e.Description}\n[REVERSED] {body.Reason!.Trim()}";

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Log("EXPENSE_REVERSED", "Expense", e.ExpenseNo, body.Reason!.Trim(), 3);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "expense", e.ExpenseId, CurrentUserId());

            /* -- C3 -- severe: money that was recorded as spent has been
               un-spent, and the books moved to make that true. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.ExpenseReversed,
                $"Expense reversed by {CurrentUserName()}",
                $"{e.ExpenseNo} -- PKR {e.Amount:N0} undone" +
                (mirrorNo is null ? "." : $" by {mirrorNo}.") + $" Reason: {body!.Reason!.Trim()}",
                url: $"/accounting/expenses/{e.ExpenseId}",
                severe: true,
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                status = "REVERSED",
                reversalEntryNo = mirrorNo,
                message = mirrorNo is null
                    ? $"{e.ExpenseNo} reversed."
                    : $"{e.ExpenseNo} reversed by {mirrorNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reverse expense {id}");
        }
    }

    /// <summary>The checks CreateExpense and UpdateExpense both need.</summary>
    private async Task<string?> ValidateExpense(ExpenseRequest body)
    {
        if (body.Amount <= 0) return "An expense needs an amount above zero.";
        if (string.IsNullOrWhiteSpace(body.VendorName)) return "Who was paid?";
        if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
            return "Pick a valid location.";
        if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.MethodId))
            return "Pick a valid payment method.";
        /* Not merely "a real account" -- the RIGHT KIND of account. Nothing
           stopped an expense being booked against Owner Capital before this,
           and the resulting entry balanced perfectly while saying something
           untrue. The screen only offers the correct accounts; this is what
           makes that a rule rather than a suggestion. */
        var expenseAccount = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountId == body.ExpenseAccountId && !a.IsGroup)
            .Select(a => new { a.AccountName, group = a.AccountType.Group.GroupName })
            .FirstOrDefaultAsync();
        if (expenseAccount is null)
            return "Pick a valid expense account. A group heading cannot take a posting.";
        if (expenseAccount.group != "Expenses")
            return $"{expenseAccount.AccountName} is a {expenseAccount.group.ToLowerInvariant()} account, not an expense account.";

        var paidFrom = await _db.Accounts.AsNoTracking()
            .Where(a => a.AccountId == body.PaidFromAccountId && !a.IsGroup)
            .Select(a => new { a.AccountName, type = a.AccountType.TypeName })
            .FirstOrDefaultAsync();
        if (paidFrom is null)
            return "Pick a valid cash or bank account to pay from.";
        if (paidFrom.type != "Cash & Bank")
            return $"{paidFrom.AccountName} is not a cash or bank account, so an expense cannot be paid from it.";

        if (body.ExpenseAccountId == body.PaidFromAccountId)
            return "The expense account and the account it is paid from cannot be the same.";
        return null;
    }

    /* ────────────────────── journal entries ────────────────────── */

    /// <summary>
    /// Replaces a draft entry's header and lines. Double entry is re-checked --
    /// an edit can unbalance an entry exactly as easily as creating one can.
    /// </summary>
    [HttpPut("journal-entries/{id:int}")]
    public async Task<IActionResult> UpdateJournalEntry(int id, [FromBody] JournalEntryRequest body)
    {
        try
        {
            var entry = await _db.JournalEntries
                .Include(e => e.JournalEntryLines)
                .Include(e => e.Status)
                .FirstOrDefaultAsync(e => e.EntryId == id);
            if (entry is null) return NotFound(new { message = $"No journal entry with id {id}." });

            var locked = WhyLocked(entry.Status.StatusKey, entry.EntryNo);
            if (locked is not null) return BadRequest(new { message = locked });

            var invalid = await ValidateJournalLines(body.Lines);
            if (invalid is not null) return BadRequest(new { message = invalid });

            var date = body.EntryDate ?? entry.EntryDate;
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (period is null) return BadRequest(new { message = $"No fiscal period covers {date:yyyy-MM-dd}." });
            if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. Reopen it or use another date." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            entry.EntryDate = date;
            entry.PeriodId = period.PeriodId;
            entry.LocationId = body.LocationId;
            entry.ReferenceNo = body.Reference;
            entry.Narration = body.Narration ?? "";

            /* Lines are replaced wholesale rather than diffed. A journal entry is
               a single statement -- keeping line ids stable across an edit buys
               nothing and costs a matching algorithm nobody would trust. */
            _db.JournalEntryLines.RemoveRange(entry.JournalEntryLines);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    EntryId = entry.EntryId,
                    LineNo = n++,
                    AccountId = l.AccountId,
                    PartyUserId = l.PartyId,
                    Description = l.Description,
                    DebitAmount = l.Debit,
                    CreditAmount = l.Credit
                });
            }

            if (body.PostImmediately)
            {
                var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
                if (posted is not null)
                {
                    entry.StatusId = posted.StatusId;
                    entry.PostedByUserId = CurrentUserId();
                }
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var total = body.Lines.Sum(l => l.Debit);
            await Log(body.PostImmediately ? "JOURNAL_POSTED" : "JOURNAL_UPDATED",
                "JournalEntry", entry.EntryNo, $"{total:N2} over {body.Lines.Count} lines", 2);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "journal-entry", entry.EntryId, CurrentUserId());

            return Ok(new
            {
                id,
                entryNo = entry.EntryNo,
                message = $"{entry.EntryNo} {(body.PostImmediately ? "updated and posted" : "updated")}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update journal entry {id}");
        }
    }

    /// <summary>Throws away a draft entry and its lines.</summary>
    [HttpDelete("journal-entries/{id:int}")]
    public async Task<IActionResult> DeleteJournalEntry(int id)
    {
        try
        {
            var entry = await _db.JournalEntries
                .Include(e => e.JournalEntryLines)
                .Include(e => e.Status)
                .FirstOrDefaultAsync(e => e.EntryId == id);
            if (entry is null) return NotFound(new { message = $"No journal entry with id {id}." });

            var locked = WhyLocked(entry.Status.StatusKey, entry.EntryNo);
            if (locked is not null) return BadRequest(new { message = locked });

            /* An entry raised BY a sale, a voucher or an expense is that
               document's audit trail. Deleting it would leave the document
               pointing at nothing. */
            if (await _db.SalesInvoices.AnyAsync(i => i.EntryId == id)
                || await _db.Vouchers.AnyAsync(v => v.EntryId == id)
                || await _db.Expenses.AnyAsync(e => e.EntryId == id)
                || await _db.SalesReturns.AnyAsync(r => r.EntryId == id))
                return BadRequest(new
                {
                    message = $"{entry.EntryNo} belongs to another document and cannot be deleted on its own."
                });

            var no = entry.EntryNo;
            _db.JournalEntryLines.RemoveRange(entry.JournalEntryLines);
            _db.JournalEntries.Remove(entry);
            await _db.SaveChangesAsync();
            await Log("JOURNAL_DELETED", "JournalEntry", no, "draft discarded", 3);

            return Ok(new { id, message = $"{no} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete journal entry {id}");
        }
    }

    /// <summary>
    /// Reverses a posted entry by writing its mirror image and marking the
    /// original REVERSED.
    ///
    /// The correction is a NEW entry, not an edit of the old one. Both stay in
    /// the ledger, so the trail reads "this was posted, then this undid it"
    /// rather than "this was never quite what you remember".
    /// </summary>
    [HttpPost("journal-entries/{id:int}/reverse")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> ReverseJournalEntry(int id, [FromBody] ReverseRequest? body)
    {
        try
        {
            var entry = await _db.JournalEntries
                .Include(e => e.JournalEntryLines)
                .Include(e => e.Status)
                .FirstOrDefaultAsync(e => e.EntryId == id);
            if (entry is null) return NotFound(new { message = $"No journal entry with id {id}." });

            if (entry.Status.StatusKey != Posted)
                return BadRequest(new
                {
                    message = $"Only a posted entry can be reversed. {entry.EntryNo} is {entry.Status.StatusName.ToLowerInvariant()} -- edit or delete it instead."
                });
            if (entry.ReversedByEntryId is not null)
            {
                var already = await _db.JournalEntries.AsNoTracking()
                    .Where(e => e.EntryId == entry.ReversedByEntryId)
                    .Select(e => e.EntryNo).FirstOrDefaultAsync();
                return BadRequest(new { message = $"{entry.EntryNo} was already reversed by {already}." });
            }

            var date = body?.ReverseDate ?? Today();
            var period = await _db.FiscalPeriods.FirstOrDefaultAsync(p => p.StartDate <= date && p.EndDate >= date);
            if (period is null) return BadRequest(new { message = $"No fiscal period covers {date:yyyy-MM-dd}." });
            if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. Reopen it or reverse into another date." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
            if (posted is null)
                return BadRequest(new { message = "No POSTED status is configured." });

            var why = string.IsNullOrWhiteSpace(body?.Reason) ? "" : $" {body!.Reason!.Trim()}";

            await using var tx = await _db.Database.BeginTransactionAsync();

            var mirror = new JournalEntry
            {
                EntryNo = await NextNumber("JV"),
                EntryDate = date,
                EntryTypeId = entry.EntryTypeId,
                PeriodId = period.PeriodId,
                LocationId = entry.LocationId,
                ReferenceNo = entry.EntryNo,
                Narration = $"Reversal of {entry.EntryNo}.{why}",
                StatusId = posted.StatusId,
                CreatedByUserId = CurrentUserId(),
                PostedByUserId = CurrentUserId(),
                CreatedAt = Today()
            };
            _db.JournalEntries.Add(mirror);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in entry.JournalEntryLines.OrderBy(l => l.LineNo))
            {
                _db.JournalEntryLines.Add(new JournalEntryLine
                {
                    EntryId = mirror.EntryId,
                    LineNo = n++,
                    AccountId = l.AccountId,
                    PartyUserId = l.PartyUserId,
                    Description = $"Reversal: {l.Description}",
                    /* The mirror: every debit becomes a credit and back. */
                    DebitAmount = l.CreditAmount,
                    CreditAmount = l.DebitAmount
                });
            }

            /* The original KEEPS its POSTED status. Every statement in this
               controller filters on POSTED, so un-posting it would drop the
               original out of the trial balance while the mirror stayed in --
               leaving the ledger holding the negative of the entry instead of
               nothing at all. Both sides count and cancel; this link is what
               tells the screen the entry was undone. */
            entry.ReversedByEntryId = mirror.EntryId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("JOURNAL_REVERSED", "JournalEntry", entry.EntryNo,
                $"reversed by {mirror.EntryNo}.{why}", 3);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "journal-entry", mirror.EntryId, CurrentUserId());

            /* -- C5 -- severe: the ledger has been moved after the fact. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.JournalReversed,
                $"Entry reversed by {CurrentUserName()}",
                $"{entry.EntryNo} was undone by {mirror.EntryNo}.{why}",
                url: $"/accounting/journal-entries/{entry.EntryId}",
                severe: true,
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                reversalId = mirror.EntryId,
                reversalNo = mirror.EntryNo,
                message = $"{entry.EntryNo} reversed by {mirror.EntryNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reverse journal entry {id}");
        }
    }

    /// <summary>The double-entry checks Create and Update both need.</summary>
    private async Task<string?> ValidateJournalLines(List<JournalLineRequest>? lines)
    {
        if (lines is null || lines.Count < 2) return "A journal entry needs at least two lines.";

        var totalDebit = lines.Sum(l => l.Debit);
        var totalCredit = lines.Sum(l => l.Credit);
        if (totalDebit != totalCredit)
            return $"Entry is out of balance: debits {totalDebit:N2} against credits {totalCredit:N2}.";
        if (totalDebit == 0) return "An entry of zero has nothing to post.";

        foreach (var l in lines)
        {
            if (l.Debit < 0 || l.Credit < 0) return "Debit and credit cannot be negative.";
            if (l.Debit > 0 && l.Credit > 0) return "A line is either a debit or a credit, never both.";
            if (l.Debit == 0 && l.Credit == 0) return "Every line needs a debit or a credit.";
            if (!await _db.Accounts.AnyAsync(a => a.AccountId == l.AccountId && !a.IsGroup))
                return $"Account {l.AccountId} is missing, or is a group heading that cannot take a posting.";
        }
        return null;
    }

    /* ─────────────────────────── vouchers ─────────────────────────── */

    /// <summary>Corrects a draft voucher, allocations included.</summary>
    [HttpPut("vouchers/{id:int}")]
    public async Task<IActionResult> UpdateVoucher(int id, [FromBody] VoucherRequest body)
    {
        try
        {
            var v = await _db.Vouchers
                .Include(x => x.Status)
                .Include(x => x.VoucherAllocations)
                .FirstOrDefaultAsync(x => x.VoucherId == id);
            if (v is null) return NotFound(new { message = $"No voucher with id {id}." });

            var locked = WhyLocked(v.Status.StatusKey, v.VoucherNo);
            if (locked is not null) return BadRequest(new { message = locked });

            var invalid = await ValidateVoucher(body);
            if (invalid is not null) return BadRequest(new { message = invalid });

            await using var tx = await _db.Database.BeginTransactionAsync();

            v.VoucherTypeId = body.VoucherTypeId;
            v.VoucherDate = body.VoucherDate ?? v.VoucherDate;
            v.LocationId = body.LocationId;
            v.PartyUserId = body.PartyId;
            v.CashBankAccountId = body.CashBankAccountId;
            v.Amount = body.Amount;
            v.MethodId = body.MethodId;
            v.PaymentProvider = body.PaymentProvider;
            v.ReferenceNo = body.Reference;
            v.WalletTxnId = body.WalletTxnId;
            v.Narration = body.Narration ?? "";

            _db.VoucherAllocations.RemoveRange(v.VoucherAllocations);
            await _db.SaveChangesAsync();

            decimal allocated = 0;
            foreach (var a in body.Allocations ?? new List<VoucherAllocationRequest>())
            {
                if (a.Amount <= 0) continue;
                allocated += a.Amount;
                _db.VoucherAllocations.Add(new VoucherAllocation
                {
                    VoucherId = v.VoucherId,
                    SalesInvoiceId = a.SalesInvoiceId,
                    PurchaseInvoiceId = a.PurchaseInvoiceId,
                    Amount = a.Amount
                });
            }

            if (allocated > body.Amount)
                return BadRequest(new
                {
                    message = $"Allocations come to {allocated:N2}, more than the voucher's {body.Amount:N2}."
                });

            if (body.PostImmediately)
            {
                var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == Posted);
                if (posted is not null) v.StatusId = posted.StatusId;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log(body.PostImmediately ? "VOUCHER_POSTED" : "VOUCHER_UPDATED",
                "Voucher", v.VoucherNo, $"{v.Amount:N2}", 2);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "voucher", v.VoucherId, CurrentUserId());

            return Ok(new
            {
                id,
                voucherNo = v.VoucherNo,
                unallocated = body.Amount - allocated,
                message = $"{v.VoucherNo} {(body.PostImmediately ? "updated and posted" : "updated")}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update voucher {id}");
        }
    }

    /// <summary>Throws away a draft voucher and its allocations.</summary>
    [HttpDelete("vouchers/{id:int}")]
    public async Task<IActionResult> DeleteVoucher(int id)
    {
        try
        {
            var v = await _db.Vouchers
                .Include(x => x.Status)
                .Include(x => x.VoucherAllocations)
                .FirstOrDefaultAsync(x => x.VoucherId == id);
            if (v is null) return NotFound(new { message = $"No voucher with id {id}." });

            var locked = WhyLocked(v.Status.StatusKey, v.VoucherNo);
            if (locked is not null) return BadRequest(new { message = locked });

            var no = v.VoucherNo;
            _db.VoucherAllocations.RemoveRange(v.VoucherAllocations);
            _db.Vouchers.Remove(v);
            await _db.SaveChangesAsync();
            await Log("VOUCHER_DELETED", "Voucher", no, $"{v.Amount:N2} draft discarded", 3);

            return Ok(new { id, message = $"{no} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete voucher {id}");
        }
    }

    /// <summary>
    /// Cancels a voucher. A posted one keeps its number and its history and is
    /// simply marked cancelled -- money that was recorded as received cannot be
    /// made to have never been recorded.
    /// </summary>
    [HttpPost("vouchers/{id:int}/cancel")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> CancelVoucher(int id, [FromBody] ReverseRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Reason))
                return BadRequest(new { message = "Cancelling a voucher needs a reason." });

            var v = await _db.Vouchers.Include(x => x.Status).FirstOrDefaultAsync(x => x.VoucherId == id);
            if (v is null) return NotFound(new { message = $"No voucher with id {id}." });

            if (v.Status.StatusKey is "CANCELLED" or "REVERSED")
                return BadRequest(new { message = $"{v.VoucherNo} is already {v.Status.StatusName.ToLowerInvariant()}." });
            if (v.Status.StatusKey == "RECONCILED")
                return BadRequest(new { message = $"{v.VoucherNo} is reconciled against a bank statement and cannot be cancelled." });

            var cancelled = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "CANCELLED");
            if (cancelled is null) return BadRequest(new { message = "No CANCELLED status is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            /* Cancelling a voucher that was never posted is just a status
               change. Cancelling a POSTED one is not -- its entry is already in
               the ledger and its allocation is already clearing an invoice, so
               both have to be undone or the customer stays credited for money
               that came back. The entry is reversed rather than deleted, for
               the same reason as everywhere else: it happened. */
            string? mirrorNo = null;
            if (v.EntryId is not null)
            {
                var original = await _db.JournalEntries
                    .Include(x => x.JournalEntryLines)
                    .FirstAsync(x => x.EntryId == v.EntryId);

                if (original.ReversedByEntryId is null)
                {
                    var date = body.ReverseDate ?? Today();
                    var period = await _db.FiscalPeriods
                        .FirstOrDefaultAsync(fp => fp.StartDate <= date && fp.EndDate >= date);
                    if (period is null) return BadRequest(new { message = $"No fiscal period covers {date:yyyy-MM-dd}." });
                    if (period.IsClosed) return BadRequest(new { message = $"{period.PeriodName} is closed. Pick another cancellation date." });

                    var mirror = new JournalEntry
                    {
                        EntryNo = await NextNumber("JV"),
                        EntryDate = date,
                        EntryTypeId = original.EntryTypeId,
                        PeriodId = period.PeriodId,
                        LocationId = original.LocationId,
                        ReferenceNo = original.EntryNo,
                        Narration = $"Reversal of {original.EntryNo} ({v.VoucherNo}) -- {body.Reason.Trim()}",
                        StatusId = original.StatusId,
                        CreatedByUserId = CurrentUserId(),
                        PostedByUserId = CurrentUserId(),
                        CreatedAt = Today()
                    };
                    _db.JournalEntries.Add(mirror);
                    await _db.SaveChangesAsync();

                    short n = 1;
                    foreach (var l in original.JournalEntryLines.OrderBy(l => l.LineNo))
                        _db.JournalEntryLines.Add(new JournalEntryLine
                        {
                            EntryId = mirror.EntryId, LineNo = n++, AccountId = l.AccountId,
                            PartyUserId = l.PartyUserId, Description = l.Description,
                            DebitAmount = l.CreditAmount,      // the swap IS the reversal
                            CreditAmount = l.DebitAmount
                        });

                    original.ReversedByEntryId = mirror.EntryId;
                    mirrorNo = mirror.EntryNo;
                }
            }

            /* The allocations go with it. A cancelled voucher must not keep
               clearing invoices -- open-invoices only counts POSTED vouchers,
               but leaving the rows behind means a later re-post would silently
               re-apply money that was taken back. */
            var allocations = await _db.VoucherAllocations.Where(a => a.VoucherId == v.VoucherId).ToListAsync();
            if (allocations.Count > 0) _db.VoucherAllocations.RemoveRange(allocations);

            v.StatusId = cancelled.StatusId;
            v.Narration = string.IsNullOrWhiteSpace(v.Narration)
                ? $"Cancelled: {body.Reason.Trim()}"
                : $"{v.Narration}\nCancelled: {body.Reason.Trim()}";

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Log("VOUCHER_CANCELLED", "Voucher", v.VoucherNo, body.Reason.Trim(), 3);

            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "voucher", v.VoucherId, CurrentUserId());

            /* -- C7 -- severe, and it must reach Sales. Money that was recorded
               as received has gone back: the invoice is owing again and the rep
               will otherwise keep telling the customer it is settled. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "sales" },
                NotificationKinds.VoucherCancelled,
                $"Payment cancelled by {CurrentUserName()}",
                $"{v.VoucherNo} -- PKR {v.Amount:N0} reversed. " +
                $"Anything it settled is outstanding again. Reason: {body.Reason.Trim()}",
                url: $"/accounting/vouchers/{v.VoucherId}",
                severe: true,
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                status = "CANCELLED",
                reversalEntryNo = mirrorNo,
                releasedAllocations = allocations.Count,
                message = mirrorNo is null
                    ? $"{v.VoucherNo} cancelled."
                    : $"{v.VoucherNo} cancelled and reversed by {mirrorNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"cancel voucher {id}");
        }
    }

    /// <summary>
    /// The double entry a posted voucher makes.
    ///
    /// A RECEIPT is money arriving: the cash or bank account is debited and
    /// Accounts Receivable is credited against the party, which is what clears
    /// their invoice. A PAYMENT is the mirror -- Accounts Payable is debited
    /// against the supplier and the cash or bank account is credited.
    ///
    /// The party goes on the control-account line as PartyUserId, exactly the
    /// way the seeded vouchers do it, so an aged-receivables report can still
    /// tell whose money this was.
    ///
    /// Sets Voucher.EntryId. Returns the refusal message, or null on success.
    /// </summary>
    private async Task<string?> PostVoucherToLedger(Voucher v)
    {
        var type = await _db.VoucherTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.VoucherTypeId == v.VoucherTypeId);
        if (type is null) return "The voucher has no type, so no entry can be written for it.";

        if (v.CashBankAccountId is null)
            return $"{v.VoucherNo} has no cash or bank account, so it cannot be posted.";

        /* The control account the party owes through, or is owed through. */
        var controlCode = type.IsReceipt ? "1130" : "2101";
        var control = await _db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AccountCode == controlCode && !a.IsGroup);
        if (control is null)
            return $"Account {controlCode} ({(type.IsReceipt ? "Accounts Receivable" : "Accounts Payable")}) is not in the chart of accounts.";

        var period = await _db.FiscalPeriods
            .FirstOrDefaultAsync(fp => fp.StartDate <= v.VoucherDate && fp.EndDate >= v.VoucherDate);
        if (period is null) return $"No fiscal period covers {v.VoucherDate:yyyy-MM-dd}, so {v.VoucherNo} cannot be posted.";
        if (period.IsClosed) return $"{period.PeriodName} is closed. {v.VoucherNo} cannot be posted into it.";

        var posted = await _db.PostingStatuses.FirstOrDefaultAsync(x => x.StatusKey == Posted);
        if (posted is null) return "No POSTED status is configured.";

        var entryType = await _db.JournalEntryTypes
            .FirstOrDefaultAsync(t => t.TypeKey == (type.IsReceipt ? "RECEIPT" : "PAYMENT"))
            ?? await _db.JournalEntryTypes.FirstAsync();

        var entry = new JournalEntry
        {
            EntryNo = await NextNumber("JV"),
            EntryDate = v.VoucherDate,
            EntryTypeId = entryType.EntryTypeId,
            PeriodId = period.PeriodId,
            LocationId = v.LocationId,
            ReferenceNo = v.VoucherNo,
            Narration = string.IsNullOrWhiteSpace(v.Narration) ? $"{type.TypeName} {v.VoucherNo}" : v.Narration,
            StatusId = posted.StatusId,
            CreatedByUserId = CurrentUserId(),
            PostedByUserId = CurrentUserId(),
            CreatedAt = Today()
        };
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();

        if (type.IsReceipt)
        {
            _db.JournalEntryLines.AddRange(
                new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = 1, AccountId = v.CashBankAccountId.Value,
                    Description = $"Received against {v.VoucherNo}",
                    DebitAmount = v.Amount, CreditAmount = 0m
                },
                new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = 2, AccountId = control.AccountId,
                    PartyUserId = v.PartyUserId,
                    Description = $"Settled by {v.VoucherNo}",
                    DebitAmount = 0m, CreditAmount = v.Amount
                });
        }
        else
        {
            _db.JournalEntryLines.AddRange(
                new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = 1, AccountId = control.AccountId,
                    PartyUserId = v.PartyUserId,
                    Description = $"Settled by {v.VoucherNo}",
                    DebitAmount = v.Amount, CreditAmount = 0m
                },
                new JournalEntryLine
                {
                    EntryId = entry.EntryId, LineNo = 2, AccountId = v.CashBankAccountId.Value,
                    Description = $"Paid against {v.VoucherNo}",
                    DebitAmount = 0m, CreditAmount = v.Amount
                });
        }
        await _db.SaveChangesAsync();

        v.EntryId = entry.EntryId;
        return null;
    }

    /// <summary>The checks CreateVoucher and UpdateVoucher both need.</summary>
    private async Task<string?> ValidateVoucher(VoucherRequest body)
    {
        if (body.Amount <= 0) return "A voucher needs an amount above zero.";
        if (!await _db.VoucherTypes.AnyAsync(t => t.VoucherTypeId == body.VoucherTypeId))
            return "Pick a valid voucher type.";
        if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
            return "Pick a valid location.";
        if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.MethodId))
            return "Pick a valid payment method.";
        if (body.PartyId is not null && !await _db.Parties.AnyAsync(p => p.UserId == body.PartyId))
            return "Pick a valid party.";
        if (body.CashBankAccountId is not null
            && !await _db.Accounts.AnyAsync(a => a.AccountId == body.CashBankAccountId && !a.IsGroup))
            return "Pick a valid cash or bank account.";
        return null;
    }

    // ══════════════════════ request bodies (part 2) ═════════════════════

    /// <summary>Approve / reject, with the reason a rejection has to carry.</summary>
    public record DecisionRequest(string StatusKey, string? Reason);

    /// <summary>Reverse or cancel: why, and -- for a reversal -- into which date.</summary>
    public record ReverseRequest(string? Reason, DateOnly? ReverseDate);

}
