using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

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
    public AccountingController(AppDbContext db, IConfiguration cfg,
        ILogger<AccountingController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

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
                    totalDebit = e.JournalEntryLines.Sum(l => (decimal?)l.DebitAmount) ?? 0m,
                    totalCredit = e.JournalEntryLines.Sum(l => (decimal?)l.CreditAmount) ?? 0m
                })
                .ToListAsync();

            return Ok(new { total, page, pageSize, items });
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
            var type = await _db.JournalEntryTypes.FirstOrDefaultAsync(t => t.TypeKey == "MANUAL")
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
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? type)
    {
        try
        {
            var rows = _db.Vouchers.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(v => v.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(type)) rows = rows.Where(v => v.VoucherType.TypeCode == type);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(v => v.VoucherNo.ToLower().Contains(term) ||
                                       v.Narration.ToLower().Contains(term) ||
                                       (v.PartyUser != null && v.PartyUser.LegalName.ToLower().Contains(term)));
            }

            return Ok(await rows
                .OrderByDescending(v => v.VoucherDate).ThenByDescending(v => v.VoucherId)
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
                .ToListAsync());
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
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
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

            return Ok(new { total = items.Sum(e => e.amount), count = items.Count, items });
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
                    entryNo = x.Entry != null ? x.Entry.EntryNo : null,
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
            if (body.Amount <= 0) return BadRequest(new { message = "An expense needs an amount above zero." });
            if (string.IsNullOrWhiteSpace(body.VendorName))
                return BadRequest(new { message = "Vendor name is required." });
            if (!await _db.Accounts.AnyAsync(a => a.AccountId == body.ExpenseAccountId && !a.IsGroup))
                return BadRequest(new { message = "Pick a valid expense account." });
            if (!await _db.Accounts.AnyAsync(a => a.AccountId == body.PaidFromAccountId && !a.IsGroup))
                return BadRequest(new { message = "Pick a valid cash or bank account to pay from." });

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
                parties = await _db.Parties.AsNoTracking()
                    .Where(p => p.User.IsActive).OrderBy(p => p.LegalName)
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = p.LegalName })
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
            await tx.CommitAsync();

            await Log(type.IsReceipt ? "RECEIPT_RECORDED" : "PAYMENT_RECORDED",
                "Voucher", v.VoucherNo, $"{body.Amount:N2}", 2);

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

            v.StatusId = posted.StatusId;
            await _db.SaveChangesAsync();
            await Log("VOUCHER_POSTED", "Voucher", v.VoucherNo, $"{v.Amount:N2}", 2);

            return Ok(new { id, message = $"{v.VoucherNo} posted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"post voucher {id}");
        }
    }

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

}
