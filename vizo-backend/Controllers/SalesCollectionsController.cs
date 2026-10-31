using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// PAYMENT COLLECTION, the salesperson's side (the owner, 1 Nov).
///
/// A rep takes money from a customer and records it here in a few taps:
/// customer, order (with its items and what is still owed), amount, and how it
/// was paid -- cash, or bank with the bank's name and the reference. It is
/// saved AWAITING: it does NOT touch the customer's ledger. The Super Admin and
/// the accountant are notified at once, and the accountant confirms it on
/// Confirm Collections, picking the account it went into (Cash on Hand, Meezan,
/// Faysal ...) -- that posts the receipt (AccountingController.ConfirmCollection).
///
/// Until it is confirmed, CollectionReminderService keeps reminding the rep to
/// hand the money in.
///
/// WHICH ORDERS (the owner, 1 Nov): every live order of the rep's customers,
/// whatever its status -- a delivered order is the usual case, but a rep can
/// take money against one still on its way, softly (the screen says it is not
/// delivered yet; nothing is refused). Cancelled, declined and returned orders
/// are not offered. An order paid in full drops off; one part-paid shows what
/// is still left, and can take another collection. "Left" is the order's total
/// (its invoice's, once there is one) less what accounts confirmed and what
/// reps already hold against it.
///
/// The list shows every collection that is the rep's: the ones they filed, and
/// any the accountant collected directly against one of their orders -- so a
/// payment confirmed without the rep ever filing it still shows here.
///
///   GET  /api/sales/collections                       the rep's collections
///   GET  /api/sales/collections/customers             customers who owe, the rep's own
///   GET  /api/sales/collections/customers/{id}/orders their open orders, with items
///   POST /api/sales/collections                       record a collection (AWAITING)
/// </summary>
[Route("api/sales/collections")]
[ApiController]
[Authorize(Policy = "Sales")]
public class SalesCollectionsController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    private static readonly string[] NotCollectable = { "CANCELLED", "DECLINED", "RETURNED" };

    public SalesCollectionsController(AppDbContext db, IConfiguration cfg,
        ILogger<SalesCollectionsController> logger, IWebHostEnvironment env, PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /// <summary>The rep's own id, or null for the Super Admin, who sees everybody's.</summary>
    private int? Mine() => CurrentRole() == OrderWorkflow.RoleSales ? CurrentUserId() : null;

    /// <summary>Live orders, any status, limited to the rep's customers and orders.</summary>
    private IQueryable<SalesOrder> OpenOrders()
    {
        var rows = _db.SalesOrders.AsNoTracking()
            .Where(o => !NotCollectable.Contains(o.Status.StatusKey));
        if (Mine() is int me)
            rows = rows.Where(o => o.SalesPersonUserId == me
                                   || o.CustomerUser.SalesPersonUserId == me
                                   || o.CustomerUser.CreatedByUserId == me);
        return rows;
    }

    private sealed record OrderRow(
        int id, string orderNo, string status, string statusName, DateOnly orderDate,
        int customerId, string customerName, string customerCode, string? city,
        int? invoiceId, string? invoiceNo, DateOnly? invoiceDate, DateOnly? dueDate,
        decimal total, decimal received, decimal awaiting)
    {
        public decimal balance => Math.Max(0, total - received);
        public decimal collectable => Math.Max(0, total - received - awaiting);
    }

    /// <summary>
    /// What each order is worth and what has come in against it, with or
    /// without an invoice. Received = confirmed collections on the order, plus
    /// posted receipts allocated to its invoice that are not a collection's own.
    /// </summary>
    private static IQueryable<OrderRow> Project(IQueryable<SalesOrder> rows) =>
        rows.Select(o => new OrderRow(
            o.OrderId, o.OrderNo, o.Status.StatusKey, o.Status.StatusName, o.OrderDate,
            o.CustomerUserId, o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName,
            o.CustomerUser.PartyCode, o.CustomerUser.City.CityName,
            o.SalesInvoice != null ? o.SalesInvoice.InvoiceId : (int?)null,
            o.SalesInvoice != null ? o.SalesInvoice.InvoiceNo : null,
            o.SalesInvoice != null ? o.SalesInvoice.InvoiceDate : (DateOnly?)null,
            o.SalesInvoice != null ? o.SalesInvoice.DueDate : (DateOnly?)null,
            o.SalesInvoice != null ? o.SalesInvoice.TotalAmount : o.TotalAmount,
            (o.CollectionAllocations.Where(a => a.Collection.Status.StatusKey == "CONFIRMED").Sum(a => (decimal?)a.Amount) ?? 0m)
              + (o.SalesInvoice != null
                  ? o.SalesInvoice.VoucherAllocations
                      .Where(v => v.Voucher.Status.StatusKey == "POSTED" && !v.Voucher.Collections.Any())
                      .Sum(v => (decimal?)v.Amount) ?? 0m
                  : 0m),
            o.CollectionAllocations.Where(a => a.Collection.Status.StatusKey == "AWAITING").Sum(a => (decimal?)a.Amount) ?? 0m));

    // ══════════════════════════════════════════════════════════════════
    //  THE LIST
    // ══════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status)
    {
        try
        {
            var rows = _db.Collections.AsNoTracking().AsQueryable();
            if (Mine() is int me)
                rows = rows.Where(c => c.CollectedByUserId == me
                                       || c.CollectionAllocations.Any(a => a.Order.SalesPersonUserId == me));
            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(c => c.Status.StatusKey == status);

            var items = await rows
                .OrderByDescending(c => c.CollectedOn).ThenByDescending(c => c.CollectionId)
                .Take(300)
                .Select(c => new
                {
                    id = c.CollectionId,
                    receiptNo = c.ReceiptNo,
                    customerId = c.CustomerUserId,
                    customerName = c.CustomerUser.DisplayName ?? c.CustomerUser.LegalName,
                    customerCode = c.CustomerUser.PartyCode,
                    collectedOn = c.CollectedOn,
                    collectedBy = c.CollectedByUser.User.FullName,
                    collectedById = c.CollectedByUserId,
                    amount = c.Amount,
                    method = c.Method.MethodKey,
                    methodName = c.Method.MethodName,
                    bank = c.BankName,
                    reference = c.ReferenceNo,
                    chequeDate = c.ChequeDate,
                    status = c.Status.StatusKey,
                    statusName = c.Status.StatusName,
                    confirmedOn = c.ConfirmedOn,
                    confirmedBy = c.ConfirmedByUser != null ? c.ConfirmedByUser.User.FullName : null,
                    depositAccountId = c.DepositAccountId,
                    voucherNo = c.Voucher != null ? c.Voucher.VoucherNo : null,
                    voucherAccount = c.Voucher != null && c.Voucher.CashBankAccount != null ? c.Voucher.CashBankAccount.AccountName : null,
                    note = c.Note,
                    orders = c.CollectionAllocations.Select(a => a.Order.OrderNo).ToList()
                })
                .ToListAsync();

            var me2 = CurrentUserId();
            var today = Today();
            return Ok(new
            {
                summary = new
                {
                    awaitingCount = items.Count(c => c.status == "AWAITING"),
                    awaitingTotal = items.Where(c => c.status == "AWAITING").Sum(c => c.amount),
                    confirmedThisMonth = items.Where(c => c.status == "CONFIRMED" && c.confirmedOn is DateOnly d
                                                          && d.Year == today.Year && d.Month == today.Month).Sum(c => c.amount)
                },
                items = items.Select(c => new
                {
                    c.id, c.receiptNo, c.customerId, c.customerName, c.customerCode, c.collectedOn,
                    c.collectedBy, byMe = c.collectedById == me2, c.amount, c.method, c.methodName, c.bank,
                    c.reference, c.chequeDate, c.status, c.statusName, c.confirmedOn, c.confirmedBy,
                    receivedInto = c.voucherAccount, c.voucherNo, c.note, c.orders
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load your collections");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE DROPDOWNS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The rep's customers with something still to collect on a live order.</summary>
    [HttpGet("customers")]
    public async Task<IActionResult> Customers()
    {
        try
        {
            var rows = await Project(OpenOrders()).ToListAsync();
            var customers = rows
                .Where(r => r.collectable > 0)
                .GroupBy(r => new { r.customerId, r.customerName, r.customerCode, r.city })
                .Select(g => new
                {
                    id = g.Key.customerId,
                    name = g.Key.customerName,
                    code = g.Key.customerCode,
                    city = g.Key.city,
                    openOrders = g.Count(),
                    owed = g.Sum(r => r.collectable),
                    awaiting = g.Sum(r => r.awaiting)
                })
                .OrderBy(c => c.name)
                .ToList();
            return Ok(new { items = customers });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the customers who owe");
        }
    }

    /// <summary>One customer's open orders, each with its items and what is still owed.</summary>
    [HttpGet("customers/{customerId:int}/orders")]
    public async Task<IActionResult> Orders(int customerId)
    {
        try
        {
            var rows = await Project(OpenOrders().Where(o => o.CustomerUserId == customerId)).ToListAsync();
            var open = rows.Where(r => r.collectable > 0).ToList();
            var ids = open.Select(r => r.id).ToList();

            var items = await _db.SalesOrderItems.AsNoTracking()
                .Where(l => ids.Contains(l.OrderId))
                .OrderBy(l => l.LineNo)
                .Select(l => new { l.OrderId, name = l.Product.ProductName, qty = l.Quantity, rate = l.UnitPrice, total = l.LineTotal })
                .ToListAsync();

            return Ok(new
            {
                /* Delivered first -- that is when the money is due -- then oldest. */
                items = open.OrderByDescending(r => r.status == OrderWorkflow.Delivered).ThenBy(r => r.orderDate).ThenBy(r => r.id).Select(r => new
                {
                    id = r.id,
                    orderNo = r.orderNo,
                    status = r.status,
                    statusName = r.statusName,
                    delivered = r.status == OrderWorkflow.Delivered,
                    orderDate = r.orderDate,
                    invoiceNo = r.invoiceNo,
                    invoiceDate = r.invoiceDate,
                    dueDate = r.dueDate,
                    total = r.total,
                    received = r.received,
                    awaiting = r.awaiting,
                    balance = r.balance,
                    /* What can still be collected: owed, less what reps already
                       hold against it and accounts has not confirmed yet. */
                    collectable = r.collectable,
                    lines = items.Where(l => l.OrderId == r.id)
                        .Select(l => new { l.name, l.qty, l.rate, l.total }).ToList()
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the open orders of customer {customerId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  RECORD
    // ══════════════════════════════════════════════════════════════════

    [HttpPost]
    public async Task<IActionResult> Record([FromBody] RecordRequest body)
    {
        try
        {
            if (body.Amount <= 0) return BadRequest(new { message = "Enter the amount you collected." });

            var row = await Project(OpenOrders().Where(o => o.OrderId == body.OrderId)).FirstOrDefaultAsync();
            if (row is null) return BadRequest(new { message = "Pick one of your orders that still owes money." });

            var collectable = row.collectable;
            if (collectable <= 0)
                return BadRequest(new { message = $"Nothing more can be collected on {row.orderNo} -- it is paid, or the rest is already waiting to be confirmed." });
            if (body.Amount > collectable)
                return BadRequest(new { message = $"{row.orderNo} can take at most PKR {collectable:N0} more." });

            /* HOW IT WAS PAID: cash, or bank. Bank needs the bank's name and
               the transaction or cheque number; a cheque date makes it a cheque. */
            var how = (body.PaidBy ?? "").Trim().ToUpperInvariant();
            if (how is not ("CASH" or "BANK"))
                return BadRequest(new { message = "Say whether the customer paid in cash or by bank." });
            var bankName = Clean(body.BankName, 60);
            var reference = Clean(body.ReferenceNo, 50);
            if (how == "BANK")
            {
                if (bankName is null) return BadRequest(new { message = "Enter the bank the customer paid from." });
                if (reference is null) return BadRequest(new { message = "Enter the transaction, slip or cheque number." });
            }
            var methodKey = how == "CASH" ? "CASH" : body.ChequeDate is not null ? "CHEQUE" : "BANK";
            var method = await _db.PaymentMethods.AsNoTracking().FirstOrDefaultAsync(m => m.MethodKey == methodKey);
            if (method is null) return BadRequest(new { message = $"The {methodKey} payment method is not configured." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only staff can record a collection." });

            var date = body.CollectedOn ?? Today();
            if (date > Today()) return BadRequest(new { message = "Money cannot be collected on a date that has not come yet." });

            var awaiting = await _db.CollectionStatuses.FirstAsync(s => s.StatusKey == "AWAITING");

            await using var tx = await _db.Database.BeginTransactionAsync();
            var c = new Collection
            {
                ReceiptNo = await NextNumber("COL"),
                CustomerUserId = row.customerId,
                CollectedByUserId = me.Value,
                CollectedOn = date,
                Amount = body.Amount,
                MethodId = method.MethodId,
                ReferenceNo = how == "BANK" ? reference : Clean(body.ReferenceNo, 50),
                BankName = how == "BANK" ? bankName : null,
                ChequeDate = methodKey == "CHEQUE" ? body.ChequeDate : null,
                StatusId = awaiting.StatusId,
                Note = Clean(body.Note, 300),
                /* The notification below is the first nudge; the reminders
                   start one interval after it. */
                RemindedAt = Now()
            };
            _db.Collections.Add(c);
            await _db.SaveChangesAsync();
            _db.CollectionAllocations.Add(new CollectionAllocation { CollectionId = c.CollectionId, OrderId = row.id, Amount = body.Amount });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("COLLECTION_RECORDED", "Collection", c.ReceiptNo, $"{row.orderNo} {body.Amount:N2} {methodKey}", 2);

            var paid = how == "CASH" ? "cash" : $"{(methodKey == "CHEQUE" ? "cheque" : "bank")} -- {bankName}, ref {reference}";
            await _push.NotifyRolesAsync(
                new[] { OrderWorkflow.RoleAdmin, OrderWorkflow.RoleAccountant },
                NotificationKinds.CollectionRecorded,
                $"Payment collected by {CurrentUserName()}",
                $"{c.ReceiptNo}: PKR {body.Amount:N0} from {row.customerName} on {row.orderNo} ({paid}). Waiting for you to confirm.",
                url: "/accounting/collections",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = c.CollectionId,
                receiptNo = c.ReceiptNo,
                message = $"{c.ReceiptNo}: PKR {body.Amount:N0} from {row.customerName} recorded. " +
                          "Hand it to the accountant -- it counts once they confirm it."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "record the collection");
        }
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>PaidBy is "CASH" or "BANK". A cheque is BANK with a ChequeDate.</summary>
    public record RecordRequest(
        int OrderId, decimal Amount, string? PaidBy, string? BankName, string? ReferenceNo,
        DateOnly? ChequeDate, DateOnly? CollectedOn, string? Note);
}
