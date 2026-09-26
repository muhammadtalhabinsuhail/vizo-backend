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
/// Customer Ledgers -- every customer's account, built from what the system
/// already records, and the statement of account the shops have been reading
/// off the old system for years.
///
/// ─────────────────────────────── WHERE THE NUMBERS COME FROM ─────────────────────
///
/// THE BOOKS, not a second copy of them. Since 26 September every sale invoice,
/// sales return and confirmed collection posts to the general ledger with the
/// customer on the Accounts Receivable line (Services/LedgerPosting.cs) and
/// receipt vouchers always did, so a customer's account IS the set of posted
/// lines on 1130 that carry their id, plus "Party"."OpeningBalance" as the
/// Balance B/F. One source: the statement, the index's balance column, the
/// trial balance's 1130 and the aged-receivables report all read the same rows
/// and cannot disagree.
///
/// The documents are read only to DESCRIBE a line: an entry that is a sale
/// invoice's shows "S# INV-26-0001" with the items indented under it, a
/// voucher's shows "RV-26-0514 · Faysal Bank Account", a hand-written row shows
/// what was typed and the item on it.
///
/// Documents that never reached the ledger (everything billed before this
/// change) are brought in by POST post-missing -- idempotent, and the index
/// page shows the owner how many are still waiting.
///
/// ─────────────────────────────── WHO ────────────────────────────────────────────
///
/// Super Admin and Accountant, by ROLE. The order desk must see no money at
/// all (the owner, 26 September), and a sales rep reads a customer's statement
/// from the Customers screen they already have -- this is the accountant's
/// ledger, with the "+" that writes to the books.
///
/// Controller-only by design: no DTOs, no services, no interfaces. Request
/// records at the foot. Every action in try/catch, reporting through Fail().
/// </summary>
[Route("api/ledgers/customers")]
[ApiController]
[Authorize(Roles = "super-admin,accountant")]
public class CustomerLedgerController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public CustomerLedgerController(AppDbContext db, IConfiguration cfg,
        ILogger<CustomerLedgerController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    private const int RoleCustomer = 5;
    private const int RoleBoth = 7;
    private const int PostedStatusId = 2;
    private const string WalkInPartyCode = "VZ-C-WALKIN";

    // ══════════════════════════════════════════════════════════════════
    //  INDEX
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every customer account with its current balance. Paged on the server --
    /// the old system had 1,504 accounts, and the import will bring them.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAccounts(
        [FromQuery] string? q, [FromQuery] int? categoryId, [FromQuery] string? sort,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 25;

            var ar = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.ReceivableCode) ?? 0;

            var rows = _db.Parties.AsNoTracking()
                .Where(p => (p.User.RoleId == RoleCustomer || p.User.RoleId == RoleBoth)
                         && p.PartyCode != WalkInPartyCode);

            if (categoryId is not null) rows = rows.Where(p => p.CategoryId == categoryId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.PartyCode.ToLower().Contains(term)
                                    || p.LegalName.ToLower().Contains(term)
                                    || (p.DisplayName != null && p.DisplayName.ToLower().Contains(term))
                                    || p.City.CityName.ToLower().Contains(term)
                                    || (p.User.Phone != null && p.User.Phone.Contains(term)));
            }

            var shaped = rows.Select(p => new
            {
                id = p.UserId,
                code = p.PartyCode,
                name = p.DisplayName ?? p.LegalName,
                legalName = p.LegalName,
                categoryId = p.CategoryId,
                category = p.Category.CategoryName,
                city = p.City.CityName,
                phone = p.User.Phone,
                creditLimit = p.CreditLimit,
                isActive = p.User.IsActive,
                salesPerson = p.SalesPersonUser != null ? p.SalesPersonUser.User.FullName : null,
                balance = p.OpeningBalance + (_db.JournalEntryLines
                    .Where(l => l.PartyUserId == p.UserId && l.AccountId == ar && l.Entry.StatusId == PostedStatusId)
                    .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m)
            });

            var total = await shaped.CountAsync();
            var totalReceivable = await shaped.SumAsync(x => (decimal?)x.balance) ?? 0m;

            shaped = (sort ?? "").ToLowerInvariant() switch
            {
                "balance" => shaped.OrderByDescending(x => x.balance).ThenBy(x => x.name),
                "code" => shaped.OrderBy(x => x.code),
                "limit" => shaped.OrderByDescending(x => x.creditLimit).ThenBy(x => x.name),
                _ => shaped.OrderBy(x => x.name)
            };

            var items = await shaped.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

            return Ok(new { total, page, pageSize, totalReceivable, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the customer ledgers");
        }
    }

    /// <summary>
    /// Everything the ledger screens' pickers need: categories (with how many
    /// accounts use each, so Remove can be greyed out before it is refused),
    /// cities, the accounts a hand-written row may balance against, products for
    /// the optional item, and the salespeople a new account can be given to.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                categories = await CategoryList(),
                cities = await _db.Cities.AsNoTracking()
                    .OrderBy(c => c.CityName)
                    .Select(c => new { id = c.CityId, name = c.CityName })
                    .ToListAsync(),
                /* The other side of a hand-written row. Any posting account
                   except Accounts Receivable itself (a row against its own
                   account would be a nothing) and the group headings. The
                   screen defaults Sale for a debit and Cash on Hand for a credit. */
                counterAccounts = await _db.Accounts.AsNoTracking()
                    .Where(a => a.IsActive && !a.IsGroup && a.AccountCode != LedgerPosting.ReceivableCode)
                    .OrderBy(a => a.AccountCode)
                    .Select(a => new { id = a.AccountId, code = a.AccountCode, name = a.AccountName, group = a.AccountType.Group.GroupName })
                    .ToListAsync(),
                defaultDebitAccountCode = LedgerPosting.SaleCode,
                defaultCreditAccountCode = LedgerPosting.CashOnHandCode,
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.ProductName)
                    .Select(p => new { id = p.ProductId, name = p.ProductName, sku = p.Sku, price = p.SalePrice })
                    .ToListAsync(),
                salesPeople = await _db.Employees.AsNoTracking()
                    .Where(e => e.User.Role.RoleKey == "sales" && e.User.IsActive)
                    .OrderBy(e => e.User.FullName)
                    .Select(e => new { id = e.UserId, name = e.User.FullName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the ledger lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STATEMENT OF ACCOUNT
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("{id:int}/statement")]
    public async Task<IActionResult> GetStatement(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var s = await BuildStatement(id, from, to);
            return s is null ? NotFound(new { message = $"No customer account with id {id}." }) : Ok(s);
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the statement for customer {id}");
        }
    }

    public sealed record StatementItem(string name, decimal qty, decimal rate, decimal amount);

    public sealed record StatementRow(
        string key, DateOnly? date, string kind, string? reference, string particulars,
        string? detail, decimal debit, decimal credit, decimal balance,
        int? entryId, string? entryNo, int? documentId, bool reversed, bool canReverse,
        List<StatementItem> items);

    public sealed record Statement(
        object account, DateOnly from, DateOnly to, decimal balanceBroughtForward,
        List<StatementRow> rows, int entryCount, decimal totalDebit, decimal totalCredit,
        decimal closingBalance, decimal creditLimit, int unpostedDocuments);

    /// <summary>
    /// Builds the statement both the screen and the PDF use -- one method, so the
    /// paper can never say something the screen did not.
    ///
    /// Default range: the first of last month to today, which is what the old
    /// statements were usually run for.
    /// </summary>
    private async Task<Statement?> BuildStatement(int id, DateOnly? from, DateOnly? to)
    {
        var party = await _db.Parties.AsNoTracking()
            .Where(p => p.UserId == id)
            .Select(p => new
            {
                id = p.UserId,
                code = p.PartyCode,
                name = p.DisplayName ?? p.LegalName,
                legalName = p.LegalName,
                category = p.Category.CategoryName,
                categoryId = p.CategoryId,
                city = p.City.CityName,
                phone = p.User.Phone,
                address = p.AddressLine,
                p.CreditLimit,
                p.CreditDays,
                p.OpeningBalance,
                salesPerson = p.SalesPersonUser != null ? p.SalesPersonUser.User.FullName : null,
                isActive = p.User.IsActive
            })
            .FirstOrDefaultAsync();
        if (party is null) return null;

        var today = Today();
        var end = to ?? today;
        var start = from ?? new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        if (start > end) (start, end) = (end, start);

        var ar = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.ReceivableCode) ?? 0;

        var mine = _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.PartyUserId == id && l.AccountId == ar && l.Entry.StatusId == PostedStatusId);

        var before = await mine.Where(l => l.Entry.EntryDate < start)
            .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
        var bf = party.OpeningBalance + before;

        var lines = await mine
            .Where(l => l.Entry.EntryDate >= start && l.Entry.EntryDate <= end)
            .OrderBy(l => l.Entry.EntryDate).ThenBy(l => l.EntryId).ThenBy(l => l.LineNo)
            .Select(l => new
            {
                l.LineId, l.EntryId, l.Description, l.DebitAmount, l.CreditAmount,
                date = l.Entry.EntryDate,
                entryNo = l.Entry.EntryNo,
                typeKey = l.Entry.EntryType.TypeKey,
                reference = l.Entry.ReferenceNo,
                narration = l.Entry.Narration,
                reversedBy = l.Entry.ReversedByEntryId,
                isMirror = l.Entry.Reverses.Any()
            })
            .ToListAsync();

        var entryIds = lines.Select(l => l.EntryId).Distinct().ToList();

        /* What each entry IS, read off the documents that point at it. */
        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.EntryId != null && entryIds.Contains(i.EntryId.Value))
            .Select(i => new
            {
                entryId = i.EntryId!.Value, i.InvoiceId, i.InvoiceNo,
                items = i.SalesInvoiceItems.OrderBy(x => x.LineNo)
                    .Select(x => new StatementItem(x.Product.ProductName, x.Quantity, x.UnitPrice, x.LineTotal)).ToList()
            })
            .ToListAsync();

        var returns = await _db.SalesReturns.AsNoTracking()
            .Where(r => r.EntryId != null && entryIds.Contains(r.EntryId.Value))
            .Select(r => new
            {
                entryId = r.EntryId!.Value, r.ReturnId, r.ReturnNo,
                items = r.SalesReturnItems.OrderBy(x => x.LineNo)
                    .Select(x => new StatementItem(x.Product.ProductName, x.Quantity, x.UnitPrice, x.Quantity * x.UnitPrice)).ToList()
            })
            .ToListAsync();

        var vouchers = await _db.Vouchers.AsNoTracking()
            .Where(v => v.EntryId != null && entryIds.Contains(v.EntryId.Value))
            .Select(v => new
            {
                entryId = v.EntryId!.Value, v.VoucherId, v.VoucherNo,
                account = v.CashBankAccount != null ? v.CashBankAccount.AccountName : null,
                method = v.Method.MethodName
            })
            .ToListAsync();

        var manualItems = await _db.LedgerEntryItems.AsNoTracking()
            .Where(x => entryIds.Contains(x.EntryId))
            .OrderBy(x => x.LineNo)
            .Select(x => new { x.EntryId, item = new StatementItem(x.ItemName, x.Qty, x.Rate, x.Amount) })
            .ToListAsync();

        /* The cash side of a counter sale, so "paid at the counter" can say where. */
        var cashLegs = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => entryIds.Contains(l.EntryId) && l.DebitAmount > 0 && l.AccountId != ar
                     && l.Account.AccountType.TypeName == "Cash & Bank")
            .Select(l => new { l.EntryId, name = l.Account.AccountName })
            .ToListAsync();

        var running = bf;
        var rows = new List<StatementRow>
        {
            new("bf", start, "opening", null, "Balance B/F", null, bf > 0 ? bf : 0, bf < 0 ? -bf : 0, bf,
                null, null, null, false, false, new List<StatementItem>())
        };

        foreach (var l in lines)
        {
            running += l.DebitAmount - l.CreditAmount;

            var inv = invoices.FirstOrDefault(x => x.entryId == l.EntryId);
            var ret = returns.FirstOrDefault(x => x.entryId == l.EntryId);
            var vch = vouchers.FirstOrDefault(x => x.entryId == l.EntryId);
            var hand = manualItems.Where(x => x.EntryId == l.EntryId).Select(x => x.item).ToList();

            string kind, particulars;
            string? detail = null;
            int? docId = null;
            var items = new List<StatementItem>();

            if (inv is not null && l.DebitAmount > 0)
            {
                kind = "sale"; particulars = $"S# {inv.InvoiceNo}"; docId = inv.InvoiceId; items = inv.items;
            }
            else if (inv is not null)
            {
                kind = "receipt"; docId = inv.InvoiceId;
                var where = cashLegs.FirstOrDefault(c => c.EntryId == l.EntryId)?.name ?? "cash";
                particulars = $"Paid at counter · {where}";
                detail = inv.InvoiceNo;
            }
            else if (ret is not null)
            {
                kind = "return"; particulars = $"SR# {ret.ReturnNo}"; docId = ret.ReturnId; items = ret.items;
            }
            else if (vch is not null)
            {
                kind = "receipt"; docId = vch.VoucherId;
                particulars = $"{vch.VoucherNo} · {vch.account ?? vch.method}";
            }
            else if (hand.Count > 0 || l.typeKey == "JOURNAL")
            {
                kind = "manual";
                particulars = string.IsNullOrWhiteSpace(l.narration) ? (l.Description ?? "Adjustment") : l.narration;
                items = hand;
            }
            else
            {
                kind = "journal";
                particulars = string.IsNullOrWhiteSpace(l.Description) ? l.narration : l.Description!;
            }

            if (l.isMirror)
            {
                kind = "reversal";
                particulars = l.narration;
                items = new List<StatementItem>();
            }

            rows.Add(new StatementRow(
                $"l{l.LineId}", l.date, kind, l.reference, particulars, detail ?? l.entryNo,
                l.DebitAmount, l.CreditAmount, running, l.EntryId, l.entryNo, docId,
                reversed: l.reversedBy is not null,
                canReverse: kind == "manual" && l.reversedBy is null && (l.reference ?? "").StartsWith("LEDGER "),
                items));
        }

        var movement = rows.Skip(1).ToList();
        var unposted = await UnpostedFor(id);

        return new Statement(
            account: new
            {
                party.id, party.code, party.name, party.legalName, party.category, party.categoryId,
                party.city, party.phone, party.address, creditLimit = party.CreditLimit,
                creditDays = party.CreditDays, openingBalance = party.OpeningBalance,
                party.salesPerson, party.isActive
            },
            from: start, to: end,
            balanceBroughtForward: bf,
            rows: rows,
            entryCount: rows.Count,
            totalDebit: rows.Sum(r => r.debit),
            totalCredit: rows.Sum(r => r.credit),
            closingBalance: running,
            creditLimit: party.CreditLimit,
            unpostedDocuments: unposted);
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE "+" ROW -- a hand-written line, posted to the books
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A row added by hand on the statement: date, description, optionally an
    /// item with a quantity and a price, and a debit OR a credit.
    ///
    /// It posts a manual journal entry -- the customer's side on Accounts
    /// Receivable with the party on the line, the other side on the account the
    /// user picked (Sale for a debit, Cash on Hand for a credit, unless they
    /// chose otherwise). LEDGER-ONLY, on purpose: no invoice, no stock
    /// movement, no document -- it is the old system's adjustment line, and the
    /// goods it names are written beside it (LedgerEntryItem) only so the
    /// statement can print them under it.
    /// </summary>
    [HttpPost("{id:int}/entries")]
    public async Task<IActionResult> AddEntry(int id, [FromBody] LedgerRowRequest body)
    {
        try
        {
            var party = await _db.Parties.AsNoTracking()
                .Where(p => p.UserId == id)
                .Select(p => new { p.UserId, p.PartyCode, name = p.DisplayName ?? p.LegalName, p.User.RoleId })
                .FirstOrDefaultAsync();
            if (party is null || (party.RoleId != RoleCustomer && party.RoleId != RoleBoth))
                return NotFound(new { message = $"No customer account with id {id}." });

            var debit = Math.Round(body.Debit ?? 0m, 2);
            var credit = Math.Round(body.Credit ?? 0m, 2);
            if (debit < 0 || credit < 0) return BadRequest(new { message = "Debit and credit cannot be negative." });
            if ((debit > 0) == (credit > 0))
                return BadRequest(new { message = "Enter a debit OR a credit -- one of them, above zero." });
            var amount = debit > 0 ? debit : credit;

            var description = (body.Description ?? "").Trim();
            var itemName = (body.ItemName ?? "").Trim();
            string? productName = null;
            if (body.ProductId is int pid)
            {
                productName = await _db.Products.AsNoTracking().Where(p => p.ProductId == pid)
                    .Select(p => p.ProductName).FirstOrDefaultAsync();
                if (productName is null) return BadRequest(new { message = "That item does not exist." });
            }
            var hasItem = productName is not null || itemName.Length > 0;
            if (hasItem)
            {
                if (body.Qty is not > 0) return BadRequest(new { message = "An item needs a quantity above zero." });
                if (body.Rate is not >= 0) return BadRequest(new { message = "An item needs a price." });
                var expected = Math.Round(body.Qty.Value * body.Rate.Value, 2);
                if (Math.Abs(expected - amount) > 0.01m)
                    return BadRequest(new
                    {
                        message = $"Quantity x price is {expected:N2}, but the {(debit > 0 ? "debit" : "credit")} is {amount:N2}. They must match."
                    });
            }
            if (description.Length == 0 && !hasItem)
                return BadRequest(new { message = "Say what the row is for -- a description or an item." });
            if (description.Length > 250) return BadRequest(new { message = "Keep the description under 250 characters." });

            var ar = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.ReceivableCode);
            if (ar is null) return BadRequest(new { message = "Account 1130 Accounts Receivable is missing from the chart." });

            int counterId;
            if (body.CounterAccountId is int chosen)
            {
                var ok = await _db.Accounts.AnyAsync(a => a.AccountId == chosen && !a.IsGroup && a.IsActive && a.AccountId != ar);
                if (!ok) return BadRequest(new { message = "Pick an account for the other side that can take a posting." });
                counterId = chosen;
            }
            else
            {
                var code = debit > 0 ? LedgerPosting.SaleCode : LedgerPosting.CashOnHandCode;
                counterId = await LedgerPosting.AccountIdAsync(_db, code)
                            ?? throw new InvalidOperationException($"Account {code} is missing from the chart.");
            }

            var date = body.Date ?? Today();
            var label = description.Length > 0 ? description
                : $"{productName ?? itemName} {body.Qty:0.##} x {body.Rate:N2}";

            var location = await _db.Locations.AsNoTracking()
                .Where(l => l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.LocationId)
                .Select(l => l.LocationId).FirstAsync();

            await using var tx = await _db.Database.BeginTransactionAsync();

            var (entry, error) = await LedgerPosting.WriteEntryAsync(_db, date, "JOURNAL", location,
                $"LEDGER {party.PartyCode}", label, CurrentUserId(), new[]
                {
                    new LedgerPosting.Leg(debit > 0 ? ar.Value : counterId, amount, 0m, label,
                        PartyUserId: debit > 0 ? party.UserId : null),
                    new LedgerPosting.Leg(debit > 0 ? counterId : ar.Value, 0m, amount, label,
                        PartyUserId: debit > 0 ? null : party.UserId)
                });
            if (entry is null) return BadRequest(new { message = error });

            if (hasItem)
            {
                _db.LedgerEntryItems.Add(new LedgerEntryItem
                {
                    EntryId = entry.EntryId,
                    LineNo = 1,
                    ProductId = body.ProductId,
                    ItemName = (productName ?? itemName).Length > 200 ? (productName ?? itemName)[..200] : (productName ?? itemName),
                    Qty = body.Qty!.Value,
                    Rate = body.Rate!.Value,
                    Amount = amount
                });
                await _db.SaveChangesAsync();
            }

            await tx.CommitAsync();

            await Log("LEDGER_ROW_POSTED", "JournalEntry", entry.EntryNo,
                $"{party.PartyCode} {(debit > 0 ? "Dr" : "Cr")} {amount:N2} -- {label}", 2);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.JournalPosted,
                $"Ledger row by {CurrentUserName()}",
                $"{party.name}: {(debit > 0 ? "debit" : "credit")} PKR {amount:N0} -- {label}.",
                url: $"/ledgers/customers/{id}",
                exceptUserId: CurrentUserId());

            return Ok(new { entryId = entry.EntryId, entryNo = entry.EntryNo, message = $"Posted as {entry.EntryNo}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the ledger row");
        }
    }

    /// <summary>
    /// Undo a hand-written row. Never a delete -- the entry is reversed by a
    /// mirror dated today, the same way AccountingController reverses any entry,
    /// so the statement reads "written, then taken back" rather than losing it.
    /// Only rows written from this screen can be undone here; a sale or a
    /// receipt is undone through its own document.
    /// </summary>
    [HttpPost("{id:int}/entries/{entryId:int}/reverse")]
    public async Task<IActionResult> ReverseEntry(int id, int entryId, [FromBody] ReverseRowRequest? body)
    {
        try
        {
            var entry = await _db.JournalEntries.Include(e => e.JournalEntryLines).Include(e => e.EntryType)
                .FirstOrDefaultAsync(e => e.EntryId == entryId);
            if (entry is null || !entry.JournalEntryLines.Any(l => l.PartyUserId == id))
                return NotFound(new { message = "That row is not on this account." });
            if (entry.EntryType.TypeKey != "JOURNAL" || !(entry.ReferenceNo ?? "").StartsWith("LEDGER "))
                return BadRequest(new { message = "Only rows written on this screen can be undone here. Sales, returns and receipts are undone through their own documents." });
            if (entry.ReversedByEntryId is not null)
                return BadRequest(new { message = $"{entry.EntryNo} has already been reversed." });

            await using var tx = await _db.Database.BeginTransactionAsync();
            var (mirror, error) = await LedgerPosting.WriteEntryAsync(_db, Today(), "JOURNAL", entry.LocationId,
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

            await Log("LEDGER_ROW_REVERSED", "JournalEntry", entry.EntryNo, mirror.EntryNo, 2);
            return Ok(new { entryId = mirror.EntryId, entryNo = mirror.EntryNo, message = $"{entry.EntryNo} reversed by {mirror.EntryNo}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"reverse entry {entryId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PDF
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The statement as a PDF, rendered on request -- what Print opens.</summary>
    [HttpGet("{id:int}/statement/pdf")]
    public async Task<IActionResult> RenderPdf(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] bool attachment = false)
    {
        try
        {
            var built = await BuildPdf(id, from, to);
            if (built is null) return NotFound(new { message = $"No customer account with id {id}." });

            Response.Headers.ContentDisposition = $"{(attachment ? "attachment" : "inline")}; filename=\"{built.Value.FileName}\"";
            return File(built.Value.Bytes, "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the statement for customer {id}");
        }
    }

    /// <summary>
    /// Renders and pushes the statement to the documents store, like every
    /// other document here -- one stored file per account and range, replaced
    /// when the same range is printed again (DocKey is the fingerprint).
    /// </summary>
    [HttpPost("{id:int}/statement/pdf")]
    public async Task<IActionResult> ArchivePdf(int id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var built = await BuildPdf(id, from, to);
            if (built is null) return NotFound(new { message = $"No customer account with id {id}." });
            var (bytes, fileName, key, code) = built.Value;

            var stored = await DocumentArchive.StoreAsync(_db, _cfg, "customer-ledger", key, code,
                fileName, bytes, CurrentUserId(), "statements");
            await Log("STATEMENT_ARCHIVED", "customer-ledger", code, stored.PdfUrl, 1);

            return Ok(new
            {
                archived = true, fileId = stored.FileId, fileName = stored.FileName,
                pdfUrl = stored.PdfUrl, isDeliverable = stored.Deliverable, bytes = stored.Bytes,
                message = $"Statement {code} saved to the document store."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the statement for customer {id}");
        }
    }

    private async Task<(byte[] Bytes, string FileName, string Key, string Code)?> BuildPdf(int id, DateOnly? from, DateOnly? to)
    {
        var s = await BuildStatement(id, from, to);
        if (s is null) return null;

        var acc = JsonSerializer.SerializeToElement(s.account);
        string Str(string n) => acc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        var extra = new List<string>();
        var catCity = string.Join("  ·  ", new[] { Str("category"), Str("city") }.Where(x => x.Length > 0));
        if (catCity.Length > 0) extra.Add(catCity);
        if (Str("phone").Length > 0) extra.Add($"Phone {Str("phone")}");

        var data = new LedgerStatementPdf.Data(
            Company: await DocumentBuilder.LetterHead(_db),
            Title: "Statement of Account",
            AccountCode: Str("code"),
            AccountName: Str("name"),
            AccountLines: extra,
            From: s.from, To: s.to,
            LimitLabel: "Ledger Limit",
            Limit: s.creditLimit,
            Lines: s.rows.Select(r => new LedgerStatementPdf.Line(
                r.date, r.particulars, r.debit, r.credit, r.balance,
                r.items.Select(i => new LedgerStatementPdf.Item(i.name, i.qty, i.rate, i.amount)).ToList(),
                Emphasis: r.kind == "opening",
                Tag: r.kind == "reversal" ? "Reversal" : r.reversed ? "Reversed" : null)).ToList(),
            EntryCount: s.entryCount,
            TotalDebit: s.totalDebit,
            TotalCredit: s.totalCredit,
            Closing: s.closingBalance,
            ClosingLabel: s.closingBalance >= 0 ? "Balance due" : "In credit",
            Footnote: "Please settle the closing balance. Contact us within 7 days if anything here is disputed.");

        var code = Str("code");
        var file = $"SOA-{code}-{s.from:yyyyMMdd}-{s.to:yyyyMMdd}.pdf";
        foreach (var bad in Path.GetInvalidFileNameChars()) file = file.Replace(bad, '-');
        return (LedgerStatementPdf.Render(data), file, $"{id}:{s.from:yyyy-MM-dd}:{s.to:yyyy-MM-dd}", code);
    }

    // ══════════════════════════════════════════════════════════════════
    //  CATEGORIES -- inline add / rename / remove
    // ══════════════════════════════════════════════════════════════════

    private async Task<object> CategoryList() =>
        await _db.PartyCategories.AsNoTracking()
            .OrderBy(c => c.CategoryId)
            .Select(c => new { id = c.CategoryId, key = c.CategoryKey, name = c.CategoryName, inUse = c.Parties.Count })
            .ToListAsync();

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories()
    {
        try { return Ok(await CategoryList()); }
        catch (Exception ex) { return Fail(ex, "load the customer categories"); }
    }

    [HttpPost("categories")]
    public async Task<IActionResult> AddCategory([FromBody] CategoryRequest body)
    {
        try
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 60) return BadRequest(new { message = "A category name is 2 to 60 characters." });
            if (await _db.PartyCategories.AnyAsync(c => c.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"There is already a category called {name}." });

            /* The key is what the code matches on (RETAILER, AGENT ...); a new
               one is the name in capitals, made unique if it has to be. */
            var baseKey = new string(name.ToUpperInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
            if (baseKey.Length == 0) baseKey = "CATEGORY";
            if (baseKey.Length > 20) baseKey = baseKey[..20];
            var key = baseKey;
            for (var n = 2; await _db.PartyCategories.AnyAsync(c => c.CategoryKey == key); n++)
                key = $"{baseKey[..Math.Min(baseKey.Length, 17)]}_{n}";

            var cat = new PartyCategory { CategoryKey = key, CategoryName = name };
            _db.PartyCategories.Add(cat);
            await _db.SaveChangesAsync();
            await Log("PARTY_CATEGORY_ADDED", "PartyCategory", key, name, 1);
            return Ok(new { id = cat.CategoryId, key, name, message = $"{name} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "add the customer category");
        }
    }

    [HttpPut("categories/{categoryId:int}")]
    public async Task<IActionResult> RenameCategory(int categoryId, [FromBody] CategoryRequest body)
    {
        try
        {
            var cat = await _db.PartyCategories.FirstOrDefaultAsync(c => c.CategoryId == categoryId);
            if (cat is null) return NotFound(new { message = "No such category." });
            var name = (body.Name ?? "").Trim();
            if (name.Length is < 2 or > 60) return BadRequest(new { message = "A category name is 2 to 60 characters." });
            if (await _db.PartyCategories.AnyAsync(c => c.CategoryId != categoryId && c.CategoryName.ToLower() == name.ToLower()))
                return BadRequest(new { message = $"There is already a category called {name}." });

            /* Renamed, never re-keyed: the key is what code and old rows match on. */
            var was = cat.CategoryName;
            cat.CategoryName = name;
            await _db.SaveChangesAsync();
            await Log("PARTY_CATEGORY_RENAMED", "PartyCategory", cat.CategoryKey, $"{was} -> {name}", 1);
            return Ok(new { id = cat.CategoryId, name, message = $"Renamed to {name}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "rename the customer category");
        }
    }

    /// <summary>
    /// Refused while any account uses it. "Party"."CategoryId" is NOT NULL with
    /// a cascading foreign key (HANDOFF trap 24) -- deleting a category in use
    /// would not fail, it would delete every customer in it.
    /// </summary>
    [HttpDelete("categories/{categoryId:int}")]
    public async Task<IActionResult> RemoveCategory(int categoryId)
    {
        try
        {
            var cat = await _db.PartyCategories.FirstOrDefaultAsync(c => c.CategoryId == categoryId);
            if (cat is null) return NotFound(new { message = "No such category." });

            var used = await _db.Parties.CountAsync(p => p.CategoryId == categoryId);
            if (used > 0)
                return BadRequest(new
                {
                    message = $"{cat.CategoryName} is used by {used} account{(used == 1 ? "" : "s")}. " +
                              "Move them to another category first -- it cannot be removed while in use."
                });

            _db.PartyCategories.Remove(cat);
            await _db.SaveChangesAsync();
            await Log("PARTY_CATEGORY_REMOVED", "PartyCategory", cat.CategoryKey, cat.CategoryName, 2);
            return Ok(new { message = $"{cat.CategoryName} removed." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "remove the customer category");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  NEW ACCOUNT -- the quick form
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens a customer account from the ledger page: name, category (required),
    /// city, phone, credit limit and opening balance. The full customer form
    /// (documents, tax numbers, rep) is still one link away; this is the
    /// accountant's "the shop exists, give it a ledger" path.
    /// </summary>
    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] QuickAccountRequest body)
    {
        try
        {
            var (id, code, error) = await OpenAccount(body.Name, body.CategoryId, body.CityId, body.Phone,
                body.CreditLimit ?? 0m, body.OpeningBalance ?? 0m, body.SalesPersonUserId, body.Code);
            if (error is not null) return BadRequest(new { message = error });

            await Log("PARTY_CREATED", "Party", code!, $"{body.Name} (ledger quick form)", 1);
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PartyAdded,
                $"Account opened by {CurrentUserName()}",
                $"{body.Name!.Trim()} ({code}) was added from Customer Ledgers.",
                url: $"/ledgers/customers/{id}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, code, message = $"{body.Name!.Trim()} opened as {code}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "open the customer account");
        }
    }

    /// <summary>
    /// The one place an account is opened from this controller -- used by the
    /// quick form and by every row of an import. Writes User and Party in one
    /// transaction, the way PartiesController.CreateParty does.
    /// </summary>
    private async Task<(int? Id, string? Code, string? Error)> OpenAccount(
        string? name, int? categoryId, int? cityId, string? phone,
        decimal creditLimit, decimal openingBalance, int? salesPersonUserId, string? wantedCode)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 2 or > 150) return (null, null, "A name is 2 to 150 characters.");
        if (categoryId is null || !await _db.PartyCategories.AnyAsync(c => c.CategoryId == categoryId))
            return (null, null, "Pick a category -- every account needs one.");
        if (cityId is null || !await _db.Cities.AnyAsync(c => c.CityId == cityId))
            return (null, null, "Pick a city.");
        if (creditLimit < 0) return (null, null, "A credit limit cannot be negative.");
        if (salesPersonUserId is not null &&
            !await _db.Employees.AnyAsync(e => e.UserId == salesPersonUserId && e.User.Role.RoleKey == "sales"))
            return (null, null, "That salesperson does not exist.");

        string code;
        if (!string.IsNullOrWhiteSpace(wantedCode))
        {
            code = wantedCode.Trim().ToUpperInvariant();
            if (code.Length > 20) return (null, null, "An account code is at most 20 characters.");
            if (await _db.Parties.AnyAsync(p => p.PartyCode.ToUpper() == code))
                return (null, null, $"Account code {code} is already in use.");
        }
        else
        {
            const string prefix = "VZ-C-";
            var used = await _db.Parties.Where(p => p.PartyCode.StartsWith(prefix)).Select(p => p.PartyCode).ToListAsync();
            var next = used.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            code = $"{prefix}{next:0000}";
        }

        var policy = await _db.CreditHoldPolicies.AsNoTracking()
            .OrderBy(h => h.PolicyKey == "WARN" ? 0 : 1).ThenBy(h => h.PolicyId)
            .Select(h => h.PolicyId).FirstAsync();

        await using var tx = await _db.Database.BeginTransactionAsync();

        var user = new User
        {
            RoleId = RoleCustomer,
            RequiresEmail = false,
            FullName = name,
            Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            IsActive = true,
            CreatedAt = Today()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.Parties.Add(new Party
        {
            UserId = user.UserId,
            PartyCode = code,
            LegalName = name,
            CategoryId = categoryId.Value,
            CityId = cityId.Value,
            CreditLimit = creditLimit,
            CreditDays = 30,
            HoldPolicyId = policy,
            OpeningBalance = Math.Round(openingBalance, 2),
            SalesPersonUserId = salesPersonUserId,
            Rating = 'C',
            CreatedByUserId = CurrentUserId()
        });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return (user.UserId, code, null);
    }

    // ══════════════════════════════════════════════════════════════════
    //  IMPORT FROM THE OLD SYSTEM
    // ══════════════════════════════════════════════════════════════════

    private static readonly string[] ImportHeaders =
        { "Code", "Name", "City", "Phone", "Category", "Opening Balance", "Credit Limit" };

    /// <summary>
    /// The template the owner fills in: the seven columns, one example row
    /// showing the shapes (a debit opening balance is positive, a customer in
    /// credit negative). Built with the same XlsxWriter as every export.
    /// </summary>
    [HttpGet("import/template")]
    public IActionResult ImportTemplate()
    {
        try
        {
            var columns = new[]
            {
                new XlsxWriter.Column("Code", "code", XlsxWriter.CellKind.Text, 14),
                new XlsxWriter.Column("Name", "name", XlsxWriter.CellKind.Text, 38),
                new XlsxWriter.Column("City", "city", XlsxWriter.CellKind.Text, 22),
                new XlsxWriter.Column("Phone", "phone", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Category", "category", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Opening Balance", "opening", XlsxWriter.CellKind.Money, 18),
                new XlsxWriter.Column("Credit Limit", "limit", XlsxWriter.CellKind.Money, 16),
            };
            var sample = JsonSerializer.SerializeToElement(new[]
            {
                new { code = "ACR00707", name = "Example Traders Hyderabad", city = "Hyderabad", phone = "0300 1234567",
                      category = "Retailer", opening = 38285m, limit = 0m }
            });
            var bytes = XlsxWriter.FromJson("Customers", sample, columns);
            return File(bytes, XlsxWriter.ContentType, "customer-import-template.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the import template");
        }
    }

    public sealed record ImportRow(
        int row, string code, string name, string city, string phone, string category,
        decimal openingBalance, decimal creditLimit,
        int? cityId, int? categoryId, List<string> errors, List<string> warnings);

    /// <summary>
    /// Reads the uploaded workbook and checks every row WITHOUT writing
    /// anything: duplicate codes and names (in the file and against the
    /// database), unknown cities and categories, numbers that are not numbers.
    /// The screen shows the result; Commit sends the good rows back.
    /// </summary>
    [HttpPost("import/preview")]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        try
        {
            if (file is null || file.Length == 0) return BadRequest(new { message = "Choose the .xlsx file to import." });
            if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only .xlsx files can be imported. Save the sheet as an Excel Workbook first." });

            List<string[]> sheet;
            await using (var s = file.OpenReadStream())
            {
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                try { sheet = XlsxReader.ReadFirstSheet(ms); }
                catch (Exception) { return BadRequest(new { message = "That file could not be read as an Excel workbook." }); }
            }

            if (sheet.Count == 0) return BadRequest(new { message = "The sheet is empty." });

            /* Columns are found by their HEADER, not their position, so a sheet
               with the columns in another order -- or an extra one -- still
               imports. */
            var head = sheet[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            int Col(params string[] names) => head.FindIndex(h => names.Contains(h));
            var cCode = Col("code", "a/c code", "account code");
            var cName = Col("name", "a/c name", "account name");
            var cCity = Col("city");
            var cPhone = Col("phone", "mobile", "contact");
            var cCat = Col("category", "type");
            var cOpen = Col("opening balance", "opening", "balance", "balance b/f");
            var cLimit = Col("credit limit", "limit", "ledger limit");
            if (cName < 0)
                return BadRequest(new { message = $"The first row must be the headers: {string.Join(", ", ImportHeaders)}." });

            var rows = sheet.Skip(1)
                .Select((r, i) => (r, line: i + 2))
                .Where(x => x.r.Any(c => !string.IsNullOrWhiteSpace(c)))
                .Select(x => new
                {
                    x.line,
                    code = Cell(x.r, cCode), name = Cell(x.r, cName), city = Cell(x.r, cCity),
                    phone = Cell(x.r, cPhone), category = Cell(x.r, cCat),
                    opening = Cell(x.r, cOpen), limit = Cell(x.r, cLimit)
                })
                .ToList();

            if (rows.Count > 5000) return BadRequest(new { message = "At most 5,000 rows can be imported at once." });

            var result = await ValidateImport(rows.Select(r => new ImportInput(
                r.line, r.code, r.name, r.city, r.phone, r.category, r.opening, r.limit)).ToList());

            return Ok(new
            {
                total = result.Count,
                ready = result.Count(r => r.errors.Count == 0),
                withErrors = result.Count(r => r.errors.Count > 0),
                openingTotal = result.Where(r => r.errors.Count == 0).Sum(r => r.openingBalance),
                rows = result
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "read the import file");
        }
    }

    /// <summary>
    /// Writes the rows the screen sends back -- re-checked here, never trusted
    /// from the preview -- one account per row, all or nothing. A row that
    /// fails the check now stops the whole import with its line number.
    /// </summary>
    [HttpPost("import/commit")]
    public async Task<IActionResult> ImportCommit([FromBody] ImportCommitRequest body)
    {
        try
        {
            var input = (body.Rows ?? new List<ImportInput>()).ToList();
            if (input.Count == 0) return BadRequest(new { message = "Nothing to import." });
            if (input.Count > 5000) return BadRequest(new { message = "At most 5,000 rows can be imported at once." });

            var checkedRows = await ValidateImport(input);
            var bad = checkedRows.FirstOrDefault(r => r.errors.Count > 0);
            if (bad is not null)
                return BadRequest(new { message = $"Row {bad.row} ({bad.name}): {bad.errors[0]} Nothing was imported." });

            await using var tx = await _db.Database.BeginTransactionAsync();
            var created = 0;
            foreach (var r in checkedRows)
            {
                var (_, _, error) = await OpenAccountNoTx(r);
                if (error is not null)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new { message = $"Row {r.row} ({r.name}): {error} Nothing was imported." });
                }
                created++;
            }
            await tx.CommitAsync();

            await Log("PARTIES_IMPORTED", "Party", $"{created} accounts",
                $"opening balances {checkedRows.Sum(r => r.openingBalance):N2}", 2);
            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.PartyAdded,
                $"Customers imported by {CurrentUserName()}",
                $"{created} accounts from the old system, opening balances PKR {checkedRows.Sum(r => r.openingBalance):N0}.",
                url: "/ledgers/customers",
                exceptUserId: CurrentUserId());

            return Ok(new { created, message = $"{created} customer accounts imported." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "import the customer accounts");
        }
    }

    /// <summary>The import's own write: same fields as OpenAccount, inside the caller's transaction.</summary>
    private async Task<(int? Id, string? Code, string? Error)> OpenAccountNoTx(ImportRow r)
    {
        var policy = await _db.CreditHoldPolicies.AsNoTracking()
            .OrderBy(h => h.PolicyKey == "WARN" ? 0 : 1).ThenBy(h => h.PolicyId)
            .Select(h => h.PolicyId).FirstAsync();

        var code = r.code.Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            const string prefix = "VZ-C-";
            var used = await _db.Parties.Where(p => p.PartyCode.StartsWith(prefix)).Select(p => p.PartyCode).ToListAsync();
            var next = used.Select(c => int.TryParse(c[prefix.Length..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            code = $"{prefix}{next:0000}";
        }

        var user = new User
        {
            RoleId = RoleCustomer, RequiresEmail = false, FullName = r.name.Trim(),
            Phone = string.IsNullOrWhiteSpace(r.phone) ? null : r.phone.Trim(),
            IsActive = true, CreatedAt = Today()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _db.Parties.Add(new Party
        {
            UserId = user.UserId, PartyCode = code, LegalName = r.name.Trim(),
            CategoryId = r.categoryId!.Value, CityId = r.cityId!.Value,
            CreditLimit = r.creditLimit, CreditDays = 30, HoldPolicyId = policy,
            OpeningBalance = r.openingBalance, Rating = 'C', CreatedByUserId = CurrentUserId()
        });
        await _db.SaveChangesAsync();
        return (user.UserId, code, null);
    }

    private static string Cell(string[] r, int i) => i >= 0 && i < r.Length ? r[i].Trim() : "";

    /// <summary>
    /// Every check an import row has to pass, for both preview and commit.
    /// Cities match on the name BEFORE " - Pakistan" as well as the full name
    /// (HANDOFF trap 22: the database spells them "Hyderabad - Pakistan", the
    /// old system spells them "Hyderabad"); categories on name or key.
    /// </summary>
    private async Task<List<ImportRow>> ValidateImport(List<ImportInput> input)
    {
        var cities = await _db.Cities.AsNoTracking().Select(c => new { c.CityId, c.CityName }).ToListAsync();
        var cityIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cities)
        {
            cityIndex.TryAdd(c.CityName.Trim(), c.CityId);
            var dash = c.CityName.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) cityIndex.TryAdd(c.CityName[..dash].Trim(), c.CityId);
        }

        var cats = await _db.PartyCategories.AsNoTracking().ToListAsync();
        var existingCodes = (await _db.Parties.AsNoTracking().Select(p => p.PartyCode).ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingNames = (await _db.Parties.AsNoTracking().Select(p => p.LegalName).ToListAsync())
            .Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retailer = cats.FirstOrDefault(c => c.CategoryKey == "RETAILER");

        var result = new List<ImportRow>();
        foreach (var r in input)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var code = (r.Code ?? "").Trim();
            var name = (r.Name ?? "").Trim();

            if (name.Length < 2) errors.Add("The name is missing.");
            else if (name.Length > 150) errors.Add("The name is longer than 150 characters.");

            if (code.Length > 20) errors.Add("The code is longer than 20 characters.");
            else if (code.Length > 0)
            {
                if (existingCodes.Contains(code)) errors.Add($"Code {code} is already in use.");
                else if (!seenCodes.Add(code)) errors.Add($"Code {code} appears twice in the file.");
            }
            else warnings.Add("No code -- one will be given (VZ-C-####).");

            if (name.Length >= 2)
            {
                if (existingNames.Contains(name)) errors.Add($"An account called {name} already exists.");
                else if (!seenNames.Add(name)) errors.Add($"{name} appears twice in the file.");
            }

            int? cityId = null;
            var city = (r.City ?? "").Trim();
            if (city.Length == 0) errors.Add("The city is missing.");
            else if (cityIndex.TryGetValue(city, out var cid)) cityId = cid;
            else errors.Add($"Unknown city: {city}. Add it at Setup first, or correct the spelling.");

            int? categoryId = null;
            var category = (r.Category ?? "").Trim();
            if (category.Length == 0)
            {
                if (retailer is not null) { categoryId = retailer.CategoryId; warnings.Add("No category -- Retailer used."); }
                else errors.Add("The category is missing.");
            }
            else
            {
                var hit = cats.FirstOrDefault(c => c.CategoryName.Equals(category, StringComparison.OrdinalIgnoreCase)
                                                || c.CategoryKey.Equals(category, StringComparison.OrdinalIgnoreCase));
                if (hit is null) errors.Add($"Unknown category: {category}. Add it on the ledger page first.");
                else categoryId = hit.CategoryId;
            }

            var opening = ParseMoney(r.OpeningBalance, "Opening balance", errors);
            var limit = ParseMoney(r.CreditLimit, "Credit limit", errors);
            if (limit < 0) errors.Add("A credit limit cannot be negative.");

            result.Add(new ImportRow(r.Row, code, name, city, (r.Phone ?? "").Trim(), category,
                opening, limit, cityId, categoryId, errors, warnings));
        }
        return result;
    }

    /// <summary>"38,285", "(1,200)", "-500.50" and blank (zero) all read as money.</summary>
    private static decimal ParseMoney(string? text, string what, List<string> errors)
    {
        var t = (text ?? "").Trim().Replace(",", "").Replace("Rs.", "", StringComparison.OrdinalIgnoreCase)
            .Replace("PKR", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (t.Length == 0) return 0m;
        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative) t = t[1..^1];
        if (decimal.TryParse(t, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v))
            return Math.Round(negative ? -v : v, 2);
        errors.Add($"{what} is not a number: {text}.");
        return 0m;
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE BACKFILL -- documents that never reached the books
    // ══════════════════════════════════════════════════════════════════

    private async Task<int> UnpostedFor(int? partyId)
    {
        var inv = _db.SalesInvoices.Where(i => i.EntryId == null && i.TotalAmount > 0);
        var ret = _db.SalesReturns.Where(r => r.EntryId == null
                                           && r.Status.StatusKey != "REJECTED" && r.Status.StatusKey != "DRAFT");
        if (partyId is not null)
        {
            inv = inv.Where(i => i.CustomerUserId == partyId);
            ret = ret.Where(r => r.CustomerUserId == partyId);
        }
        return await inv.CountAsync() + await ret.CountAsync();
    }

    /// <summary>How many documents are still waiting to post -- shown on the index page.</summary>
    [HttpGet("posting-status")]
    public async Task<IActionResult> PostingStatus()
    {
        try
        {
            var invoices = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.EntryId == null && i.TotalAmount > 0)
                .Select(i => new { i.InvoiceNo, i.InvoiceDate, i.TotalAmount }).ToListAsync();
            var returns = await _db.SalesReturns.AsNoTracking()
                .Where(r => r.EntryId == null && r.Status.StatusKey != "REJECTED" && r.Status.StatusKey != "DRAFT")
                .Select(r => new { r.ReturnNo, r.ReturnDate }).ToListAsync();
            var collections = await _db.Collections.AsNoTracking()
                .Where(c => c.VoucherId == null && c.Status.StatusKey == "CONFIRMED")
                .Select(c => new { c.ReceiptNo, c.CollectedOn, c.Amount }).ToListAsync();

            return Ok(new
            {
                invoices = invoices.Count, invoiceTotal = invoices.Sum(i => i.TotalAmount),
                returns = returns.Count, collections = collections.Count,
                pending = invoices.Count + returns.Count + collections.Count
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "count the unposted documents");
        }
    }

    /// <summary>
    /// Posts every sale invoice, sales return and confirmed collection that has
    /// no journal entry yet -- the one-off that closes gap D7 for the history.
    /// IDEMPOTENT: each document is skipped once it has an EntryId, so running
    /// it twice posts nothing the second time. Each document is its own
    /// transaction, so one that cannot post (a closed month) is reported and
    /// the rest still go through.
    ///
    /// A bill already marked PAID with no receipt against it was paid at the
    /// counter (every one on live is a counter sale): it posts with its
    /// payment, into the cash or bank account of its method.
    ///
    /// Super Admin only -- it writes history in bulk.
    /// </summary>
    [HttpPost("post-missing")]
    [Authorize(Roles = "super-admin")]
    public async Task<IActionResult> PostMissing()
    {
        try
        {
            var me = CurrentUserId();
            var done = new List<string>();
            var failed = new List<string>();

            var invoices = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.EntryId == null && i.TotalAmount > 0)
                .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceId)
                .Select(i => new
                {
                    i.InvoiceId, i.InvoiceNo, status = i.Status.StatusKey, method = i.Method.MethodKey,
                    receipted = i.VoucherAllocations.Any()
                })
                .ToListAsync();

            foreach (var i in invoices)
            {
                var paidAtCounter = i.status == "PAID" && !i.receipted && LedgerPosting.CashAccountCodeFor(i.method) is not null;
                await using var tx = await _db.Database.BeginTransactionAsync();
                var why = await LedgerPosting.PostSalesInvoiceAsync(_db, i.InvoiceId, me, paidAtCounter ? i.method : null);
                if (why is null) { await tx.CommitAsync(); done.Add(i.InvoiceNo); }
                else { await tx.RollbackAsync(); failed.Add(why); _db.ChangeTracker.Clear(); }
            }

            var returns = await _db.SalesReturns.AsNoTracking()
                .Where(r => r.EntryId == null && r.Status.StatusKey != "REJECTED" && r.Status.StatusKey != "DRAFT")
                .OrderBy(r => r.ReturnDate).Select(r => new { r.ReturnId, r.ReturnNo }).ToListAsync();
            foreach (var r in returns)
            {
                await using var tx = await _db.Database.BeginTransactionAsync();
                var why = await LedgerPosting.PostSalesReturnAsync(_db, r.ReturnId, me);
                if (why is null) { await tx.CommitAsync(); done.Add(r.ReturnNo); }
                else { await tx.RollbackAsync(); failed.Add(why); _db.ChangeTracker.Clear(); }
            }

            var collections = await _db.Collections.AsNoTracking()
                .Where(c => c.VoucherId == null && c.Status.StatusKey == "CONFIRMED")
                .Select(c => new { c.CollectionId, c.ReceiptNo }).ToListAsync();
            foreach (var c in collections)
            {
                await using var tx = await _db.Database.BeginTransactionAsync();
                var why = await LedgerPosting.PostCollectionAsync(_db, c.CollectionId, me);
                if (why is null) { await tx.CommitAsync(); done.Add(c.ReceiptNo); }
                else { await tx.RollbackAsync(); failed.Add(why); _db.ChangeTracker.Clear(); }
            }

            /* "ActivityLog"."Detail" is varchar(300): the list is cut to fit. */
            var logged = failed.Count == 0 ? string.Join(", ", done) : string.Join(" | ", failed);
            await Log("LEDGER_BACKFILL", "JournalEntry", $"{done.Count} posted",
                logged.Length > 290 ? logged[..287] + "..." : logged, 2);

            return Ok(new
            {
                posted = done.Count,
                failed = failed.Count,
                documents = done,
                problems = failed,
                message = failed.Count == 0
                    ? $"{done.Count} document{(done.Count == 1 ? "" : "s")} posted to the books."
                    : $"{done.Count} posted, {failed.Count} could not be: {failed[0]}"
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "post the missing documents");
        }
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record LedgerRowRequest(
        DateOnly? Date, string? Description, int? ProductId, string? ItemName,
        decimal? Qty, decimal? Rate, decimal? Debit, decimal? Credit, int? CounterAccountId);

    public record ReverseRowRequest(string? Reason);

    public record CategoryRequest(string? Name);

    public record QuickAccountRequest(
        string? Name, int? CategoryId, int? CityId, string? Phone,
        decimal? CreditLimit, decimal? OpeningBalance, int? SalesPersonUserId, string? Code);

    public record ImportInput(
        int Row, string? Code, string? Name, string? City, string? Phone, string? Category,
        string? OpeningBalance, string? CreditLimit);

    public record ImportCommitRequest(List<ImportInput>? Rows);
}
