using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// CONFIRM COLLECTIONS, made to do what it says (27 Sep).
///
/// The owner: every order that has moved past approval ("jin ka status patch
/// ho chuka hai") shows up here as a row; opening it asks how much is being
/// collected and confirmed now; confirming writes it to the customer's account,
/// so his ledger shows what he has paid and what is still owed, debit and
/// credit, like every other entry.
///
/// WHAT WAS THERE. The screen listed collections a rep had recorded, with a
/// Confirm button. But nothing in the API ever CREATED a collection -- the
/// eight on live were seed rows, and the "record collection" dialog only showed
/// a toast. So the page could only ever confirm the same eight.
///
/// WHAT IS HERE NOW, on the same URL prefix as the old list (AccountingController
/// keeps GET collections and POST collections/{id}/confirm):
///   GET  receivables          every invoiced order still owing, with what is
///                             received and what reps say they hold
///   GET  orders/{orderId}     what the modal needs: the order, its receipts,
///                             the customer's whole account, the ways to pay
///   POST collect              collect now: a CONFIRMED collection, allocated to
///                             the order, posted as a receipt voucher (Dr the
///                             cash/bank account of the method, Cr the customer)
///                             and allocated to the invoice -- LedgerPosting.
///
/// "Received" on an order = confirmed collections allocated to it, plus any
/// posted receipt voucher allocated to its invoice that is NOT a collection's
/// own voucher (so nothing is counted twice).
/// </summary>
[Route("api/accounting/collections")]
[ApiController]
[Authorize(Roles = "super-admin,accountant")]
public class CollectionDeskController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    /* Orders that owe nothing yet (no invoice) or never will (cancelled,
       declined, returned whole) are not collectable. */
    private static readonly string[] NotCollectable = { "CANCELLED", "DECLINED", "RETURNED", "DRAFT" };

    public CollectionDeskController(AppDbContext db, IConfiguration cfg,
        ILogger<CollectionDeskController> logger, IWebHostEnvironment env, PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  THE ROWS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("receivables")]
    public async Task<IActionResult> Receivables(
        [FromQuery] string? q, [FromQuery] string? show, [FromQuery] int? customerId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 30;

            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => o.SalesInvoice != null && !NotCollectable.Contains(o.Status.StatusKey));
            if (customerId is not null) rows = rows.Where(o => o.CustomerUserId == customerId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(o => o.OrderNo.ToLower().Contains(term)
                    || o.SalesInvoice!.InvoiceNo.ToLower().Contains(term)
                    || (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName).ToLower().Contains(term)
                    || o.CustomerUser.PartyCode.ToLower().Contains(term));
            }

            var list = await Project(rows).ToListAsync();

            /* "open" (default) = still owing; "paid" = settled; "all". */
            var filtered = (show ?? "open").ToLowerInvariant() switch
            {
                "paid" => list.Where(r => r.balance <= 0).ToList(),
                "all" => list,
                _ => list.Where(r => r.balance > 0).ToList()
            };

            var ordered = filtered.OrderByDescending(r => r.balance > 0).ThenBy(r => r.invoiceDate).ThenBy(r => r.id).ToList();
            var today = Today();
            return Ok(new
            {
                total = ordered.Count,
                page, pageSize,
                summary = new
                {
                    openOrders = list.Count(r => r.balance > 0),
                    outstanding = list.Where(r => r.balance > 0).Sum(r => r.balance),
                    overdue = list.Where(r => r.balance > 0 && r.dueDate < today).Sum(r => r.balance),
                    heldByReps = list.Sum(r => r.awaiting),
                    receivedThisMonth = await _db.Collections.AsNoTracking()
                        .Where(c => c.Status.StatusKey == "CONFIRMED" && c.ConfirmedOn != null
                                    && c.ConfirmedOn.Value.Year == today.Year && c.ConfirmedOn.Value.Month == today.Month)
                        .SumAsync(c => (decimal?)c.Amount) ?? 0m
                },
                items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(r => Shape(r, today))
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the orders to collect");
        }
    }

    private sealed record Row(
        int id, string orderNo, string status, string statusName, DateOnly orderDate,
        int customerId, string customerName, string customerCode, string? city, string? salesPerson,
        int invoiceId, string invoiceNo, DateOnly invoiceDate, DateOnly dueDate, decimal total,
        decimal collected, decimal viaVouchers, decimal awaiting, decimal balance);

    private IQueryable<Row> Project(IQueryable<SalesOrder> rows) =>
        rows.Select(o => new
            {
                o,
                inv = o.SalesInvoice!,
                collected = o.CollectionAllocations
                    .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                    .Sum(a => (decimal?)a.Amount) ?? 0m,
                viaVouchers = o.SalesInvoice!.VoucherAllocations
                    .Where(v => v.Voucher.Status.StatusKey == "POSTED" && !v.Voucher.Collections.Any())
                    .Sum(v => (decimal?)v.Amount) ?? 0m,
                awaiting = o.CollectionAllocations
                    .Where(a => a.Collection.Status.StatusKey == "AWAITING")
                    .Sum(a => (decimal?)a.Amount) ?? 0m
            })
            .Select(x => new Row(
                x.o.OrderId, x.o.OrderNo, x.o.Status.StatusKey, x.o.Status.StatusName, x.o.OrderDate,
                x.o.CustomerUserId, x.o.CustomerUser.DisplayName ?? x.o.CustomerUser.LegalName,
                x.o.CustomerUser.PartyCode, x.o.CustomerUser.City.CityName,
                x.o.SalesPersonUser != null ? x.o.SalesPersonUser.User.FullName : null,
                x.inv.InvoiceId, x.inv.InvoiceNo, x.inv.InvoiceDate, x.inv.DueDate, x.inv.TotalAmount,
                x.collected, x.viaVouchers, x.awaiting,
                x.inv.TotalAmount - x.collected - x.viaVouchers));

    private static object Shape(Row r, DateOnly today) => new
    {
        r.id, r.orderNo, r.status, r.statusName, r.orderDate,
        r.customerId, r.customerName, customerInitials = Initials(r.customerName), r.customerCode, r.city, r.salesPerson,
        r.invoiceId, r.invoiceNo, r.invoiceDate, r.dueDate, r.total,
        received = r.collected + r.viaVouchers,
        r.awaiting,
        balance = Math.Max(0, r.balance),
        paymentStatus = r.balance <= 0 ? "PAID" : r.collected + r.viaVouchers > 0 ? "PARTIAL" : "UNPAID",
        daysOverdue = r.balance > 0 && r.dueDate < today ? today.DayNumber - r.dueDate.DayNumber : 0
    };

    // ══════════════════════════════════════════════════════════════════
    //  THE MODAL
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("orders/{orderId:int}")]
    public async Task<IActionResult> Order(int orderId)
    {
        try
        {
            var row = await Project(_db.SalesOrders.AsNoTracking().Where(o => o.OrderId == orderId && o.SalesInvoice != null))
                .FirstOrDefaultAsync();
            if (row is null) return NotFound(new { message = "That order has no invoice yet, so nothing is owed on it." });

            var today = Today();

            /* Every receipt against this order, newest first -- confirmed and waiting. */
            var receipts = await _db.CollectionAllocations.AsNoTracking()
                .Where(a => a.OrderId == orderId)
                .OrderByDescending(a => a.Collection.CollectedOn).ThenByDescending(a => a.CollectionId)
                .Select(a => new
                {
                    id = a.CollectionId,
                    receiptNo = a.Collection.ReceiptNo,
                    date = a.Collection.CollectedOn,
                    amount = a.Amount,
                    method = a.Collection.Method.MethodName,
                    status = a.Collection.Status.StatusKey,
                    statusName = a.Collection.Status.StatusName,
                    collectedBy = a.Collection.CollectedByUser.User.FullName,
                    voucherNo = a.Collection.Voucher != null ? a.Collection.Voucher.VoucherNo : null,
                    reference = a.Collection.ReferenceNo
                })
                .ToListAsync();

            /* The customer's whole account, off the books (the same sum the
               customer ledger shows): opening balance + what the ledger says
               he owes on 1130, net of everything he has paid or returned. */
            var account = await CustomerAccountAsync(row.customerId);

            return Ok(new
            {
                order = Shape(row, today),
                receipts,
                account,
                methods = await ReceivingMethodsAsync(),
                collectors = await CollectorsAsync(),
                defaultCollectorId = await _db.SalesOrders.AsNoTracking().Where(o => o.OrderId == orderId)
                    .Select(o => _db.Employees.Any(e => e.UserId == o.SalesPersonUserId) ? (int?)o.SalesPersonUserId : null)
                    .FirstOrDefaultAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {orderId} for collection");
        }
    }

    private async Task<object> CustomerAccountAsync(int customerId)
    {
        var ar = await LedgerPosting.AccountIdAsync(_db, "1130");
        var party = await _db.Parties.AsNoTracking().Where(p => p.UserId == customerId)
            .Select(p => new { p.PartyCode, name = p.DisplayName ?? p.LegalName, p.OpeningBalance, p.CreditLimit })
            .FirstAsync();
        var lines = _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == ar && l.PartyUserId == customerId && l.Entry.Status.StatusKey == "POSTED");
        var debits = await lines.SumAsync(l => (decimal?)l.DebitAmount) ?? 0m;
        var credits = await lines.SumAsync(l => (decimal?)l.CreditAmount) ?? 0m;
        return new
        {
            code = party.PartyCode,
            party.name,
            opening = party.OpeningBalance,
            billed = debits,
            paid = credits,
            balance = party.OpeningBalance + debits - credits,
            creditLimit = party.CreditLimit
        };
    }

    /* The ways a collection can come in: every active method that lands in a
       real cash, bank or wallet account (not "Credit"). The account is named so
       the accountant can see where the money will show. */
    private async Task<List<object>> ReceivingMethodsAsync()
    {
        var methods = await _db.PaymentMethods.AsNoTracking().Where(m => m.IsActive)
            .OrderBy(m => m.MethodId).Select(m => new { m.MethodId, m.MethodKey, m.MethodName }).ToListAsync();
        var result = new List<object>();
        foreach (var m in methods)
        {
            var code = LedgerPosting.CashAccountCodeFor(m.MethodKey);
            if (code is null || m.MethodKey == "PETTY_CASH") continue;
            var acc = await _db.Accounts.AsNoTracking().Where(a => a.AccountCode == code && !a.IsGroup && a.IsActive)
                .Select(a => new { a.AccountCode, a.AccountName }).FirstOrDefaultAsync();
            if (acc is null) continue;
            result.Add(new
            {
                id = m.MethodId, key = m.MethodKey, name = m.MethodName,
                account = $"{acc.AccountCode} {acc.AccountName}",
                needsReference = m.MethodKey is not "CASH",
                isCheque = m.MethodKey == "CHEQUE"
            });
        }
        return result;
    }

    /* Who physically took the money: staff with an Employee row (the FK). */
    private async Task<List<object>> CollectorsAsync() =>
        (await _db.Employees.AsNoTracking()
            .Where(e => e.User.IsActive && !e.IsLocked)
            .OrderBy(e => e.User.FullName)
            .Select(e => new { id = e.UserId, name = e.User.FullName, role = e.User.Role.RoleName })
            .ToListAsync()).Cast<object>().ToList();

    // ══════════════════════════════════════════════════════════════════
    //  COLLECT
    // ══════════════════════════════════════════════════════════════════

    [HttpPost("collect")]
    public async Task<IActionResult> Collect([FromBody] CollectRequest body)
    {
        try
        {
            if (body.Amount <= 0) return BadRequest(new { message = "Enter the amount received." });

            var row = await Project(_db.SalesOrders.AsNoTracking().Where(o => o.OrderId == body.OrderId && o.SalesInvoice != null))
                .FirstOrDefaultAsync();
            if (row is null) return BadRequest(new { message = "That order has no invoice yet, so nothing is owed on it." });
            if (NotCollectable.Contains(row.status))
                return BadRequest(new { message = $"{row.orderNo} is {row.statusName} -- nothing is collectable on it." });
            if (row.balance <= 0) return BadRequest(new { message = $"{row.orderNo} is already fully paid." });
            if (body.Amount > row.balance)
                return BadRequest(new { message = $"{row.orderNo} only owes {row.balance:N2}. Collect that much or less." });

            var method = await _db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.MethodId == body.MethodId && m.IsActive);
            if (method is null || LedgerPosting.CashAccountCodeFor(method.MethodKey) is null)
                return BadRequest(new { message = "Pick how the money came in." });
            if (method.MethodKey != "CASH" && string.IsNullOrWhiteSpace(body.ReferenceNo))
                return BadRequest(new { message = $"{method.MethodName} needs its reference (transaction, slip or cheque number)." });

            var date = body.CollectedOn ?? Today();
            if (date > Today()) return BadRequest(new { message = "Money cannot be received on a date that has not come yet." });

            var collector = body.CollectedByUserId ?? CurrentUserId();
            if (!await _db.Employees.AnyAsync(e => e.UserId == collector))
                return BadRequest(new { message = "Pick who took the money (a staff member)." });

            var confirmed = await _db.CollectionStatuses.FirstAsync(s => s.StatusKey == "CONFIRMED");
            var me = await CurrentEmployeeId();

            await using var tx = await _db.Database.BeginTransactionAsync();

            var c = new Collection
            {
                ReceiptNo = await NextNumber("COL"),
                CustomerUserId = row.customerId,
                CollectedByUserId = collector,
                CollectedOn = date,
                Amount = body.Amount,
                MethodId = method.MethodId,
                ReferenceNo = Clean(body.ReferenceNo, 50),
                BankName = Clean(body.BankName, 60),
                ChequeDate = method.MethodKey == "CHEQUE" ? body.ChequeDate : null,
                StatusId = confirmed.StatusId,
                ConfirmedOn = Today(),
                ConfirmedByUserId = me,
                Note = Clean(body.Note, 300)
            };
            _db.Collections.Add(c);
            await _db.SaveChangesAsync();
            _db.CollectionAllocations.Add(new CollectionAllocation { CollectionId = c.CollectionId, OrderId = row.id, Amount = body.Amount });
            await _db.SaveChangesAsync();

            /* The books: a receipt voucher into the method's account, credited to
               the customer, allocated to this order's invoice. */
            var error = await LedgerPosting.PostCollectionAsync(_db, c.CollectionId, CurrentUserId());
            if (error is not null) return BadRequest(new { message = error });

            await tx.CommitAsync();

            var voucherNo = await _db.Vouchers.AsNoTracking().Where(v => v.VoucherId == c.VoucherId).Select(v => v.VoucherNo).FirstOrDefaultAsync();
            var left = row.balance - body.Amount;
            await Log("COLLECTION_RECEIVED", "Collection", c.ReceiptNo,
                $"{row.orderNo} {body.Amount:N2} {method.MethodKey} -> {voucherNo}", 2);

            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.CollectionConfirmed,
                $"Payment received: {row.customerName}",
                $"{c.ReceiptNo} -- PKR {body.Amount:N0} on {row.orderNo} ({method.MethodName}). " +
                (left <= 0 ? "Paid in full." : $"PKR {left:N0} still owed."),
                url: $"/ledgers/customers/{row.customerId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: new[] { collector });

            return Ok(new
            {
                id = c.CollectionId, receiptNo = c.ReceiptNo, voucherNo,
                balance = Math.Max(0, left),
                message = $"{c.ReceiptNo}: PKR {body.Amount:N0} received on {row.orderNo} and posted as {voucherNo}." +
                          (left <= 0 ? " The order is paid in full." : $" PKR {left:N0} is still owed on it.")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "record the collection");
        }
    }

    /// <summary>
    /// A rep's collection that never arrived -- a cheque that bounced, cash that
    /// was not handed in. Only while it is still AWAITING: it has not touched the
    /// books, so marking it is the whole job. A CONFIRMED one has posted a receipt
    /// and needs that receipt reversed instead (Journal Entries).
    /// </summary>
    [HttpPost("{id:int}/bounce")]
    public async Task<IActionResult> Bounce(int id, [FromBody] BounceRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return BadRequest(new { message = "Say what happened." });
            var c = await _db.Collections.Include(x => x.Status).FirstOrDefaultAsync(x => x.CollectionId == id);
            if (c is null) return NotFound(new { message = $"No collection with id {id}." });
            if (c.Status.StatusKey != "AWAITING")
                return BadRequest(new { message = $"{c.ReceiptNo} is {c.Status.StatusName}; only one still awaiting can be marked bounced." });

            var bounced = await _db.CollectionStatuses.FirstAsync(s => s.StatusKey == "BOUNCED");
            c.StatusId = bounced.StatusId;
            var why = $"Bounced: {body.Reason.Trim()}";
            c.Note = Clean(string.IsNullOrWhiteSpace(c.Note) ? why : $"{c.Note} {why}", 300);
            await _db.SaveChangesAsync();
            await Log("COLLECTION_BOUNCED", "Collection", c.ReceiptNo, body.Reason.Trim(), 2);

            await _push.NotifyRolesAsync(
                new[] { "super-admin" },
                NotificationKinds.CollectionConfirmed,
                $"Collection bounced: {c.ReceiptNo}",
                $"PKR {c.Amount:N0} -- {body.Reason.Trim()}",
                url: "/accounting/collections",
                exceptUserId: CurrentUserId(),
                alsoUserIds: new[] { c.CollectedByUserId });

            return Ok(new { id, message = $"{c.ReceiptNo} marked bounced. The customer's balance never moved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"mark collection {id} bounced");
        }
    }

    public record BounceRequest(string? Reason);

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    public record CollectRequest(
        int OrderId, decimal Amount, int MethodId, DateOnly? CollectedOn, int? CollectedByUserId,
        string? ReferenceNo, string? BankName, DateOnly? ChequeDate, string? Note);
}
