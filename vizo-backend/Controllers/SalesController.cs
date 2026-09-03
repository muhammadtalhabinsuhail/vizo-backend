using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /sales screens: orders, invoices, returns, credit holds and counter sale.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match exactly what each screen
/// renders. Every action is wrapped in try/catch and reports through Fail().
///
/// TRAPS worth knowing before editing the queries:
///   * SalesOrder.CustomerUser is a PARTY, not a User -- the trading name is
///     .CustomerUser.LegalName.
///   * SalesOrder.SalesPersonUser is an EMPLOYEE, so the person's name is one
///     hop further on at .SalesPersonUser.User.FullName.
///   * SalesOrder.CreatedByUser IS a User (the purchase side is the opposite --
///     see PurchasesController).
///   * The order document series is "ORD", NOT "SO". Getting that wrong does
///     not throw: NextNumber falls back to a timestamp, so orders quietly
///     number SO-20260829174238 instead of ORD-26-0144.
///
/// THE BILL. Every invoice this controller raises is also rendered to a PDF
/// (Documents/InvoicePdf.cs), pushed to the documents Cloudinary account and
/// the link stored on the row. That link is what the WhatsApp share sends and
/// what Download serves, so it has to outlive the request that made it. If
/// Cloudinary is unreachable the sale still completes -- the money and the
/// stock matter, a re-buildable PDF does not.
/// </summary>
[Route("api/sales")]
[ApiController]
[Authorize(Policy = "Staff")]
public class SalesController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public SalesController(AppDbContext db, IConfiguration cfg,
        ILogger<SalesController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /// <summary>The shared party every walk-in counter sale is booked against.</summary>
    private const string WalkInPartyCode = "VZ-C-WALKIN";

    // ══════════════════════════════════════════════════════════════════
    //  ORDERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? customerId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.SalesOrders.AsNoTracking().AsQueryable();

            /* A salesperson sees THEIR OWN work and nothing else.

               This is not a convenience -- it is the rule. Giving the Sales role
               the right to handle returns must not turn into the right to read
               every other rep's customers and prices. Accounts and the admin
               see everything, because reconciling the books needs everything. */
            if (CurrentRole() == OrderWorkflow.RoleSales)
            {
                var me = CurrentUserId();
                rows = rows.Where(o => o.SalesPersonUserId == me || o.CreatedByUserId == me);
            }

            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(o => o.Status.StatusKey == status);
            if (customerId is not null)
                rows = rows.Where(o => o.CustomerUserId == customerId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(o => o.OrderNo.ToLower().Contains(term) ||
                                       o.CustomerUser.LegalName.ToLower().Contains(term));
            }

            var total = await rows.CountAsync();

            var items = await rows
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.OrderId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = o.CustomerUser.LegalName,
                    customerType = o.CustomerUser.Category.CategoryName,
                    city = o.CustomerUser.City.CityName,
                    location = o.Location.LocationName,
                    locationCode = o.Location.LocationCode,
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : null,
                    orderDate = o.OrderDate,
                    deliveryDate = o.DeliveryDate,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    itemCount = o.SalesOrderItems.Count,
                    subtotal = o.Subtotal,
                    discount = o.DiscountAmount,
                    tax = o.TaxAmount,
                    total = o.TotalAmount,
                    paymentMethod = o.Method.MethodKey,
                    creditHoldReason = o.CreditHoldReason,
                    notes = o.Notes,

                    /* Paid = confirmed collections allocated to this order.
                       Unconfirmed rep cash deliberately does NOT count -- that
                       is the control gap the business asked for. */
                    paidAmount = o.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m,

                    invoiceId = o.SalesInvoice != null ? (int?)o.SalesInvoice.InvoiceId : null,
                    invoiceNo = o.SalesInvoice != null ? o.SalesInvoice.InvoiceNo : null,

                    channel = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Channel.ChannelKey).FirstOrDefault(),
                    carrier = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Courier != null ? d.Courier.CourierName : null).FirstOrDefault(),
                    trackingNo = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.TrackingNo).FirstOrDefault(),
                    deliveryState = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Status.StatusKey).FirstOrDefault(),
                    dispatchedOn = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.BookedDate).FirstOrDefault(),
                    deliveredOn = o.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.DeliveredDate).FirstOrDefault()
                })
                .ToListAsync();

            var shaped = items.Select(o => new
            {
                o.id, o.orderNo, o.customerId, o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerType, o.city, o.location, o.locationCode, o.salesPerson,
                o.orderDate, o.deliveryDate, o.status, o.statusName, o.itemCount,
                o.subtotal, o.discount, o.tax, o.total,
                o.paymentMethod, o.paidAmount,
                paymentStatus = o.paidAmount <= 0 ? "UNPAID"
                              : o.paidAmount >= o.total ? "PAID" : "PARTIAL",
                o.creditHoldReason, o.notes, o.invoiceId, o.invoiceNo,
                o.channel, o.carrier, o.trackingNo, o.deliveryState,
                o.dispatchedOn, o.deliveredOn
            });

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the order list");
        }
    }

    /// <summary>
    /// One order, with everything the detail screen puts on the page: its real
    /// lines, what has been paid, where the delivery got to, the invoice if one
    /// exists, and the activity trail.
    ///
    /// The activity used to be an invented array in the browser -- it read
    /// "System emailed PO to supplier" on every order whether or not anything
    /// had been emailed. It now comes off ActivityLog, keyed on the order
    /// number, which means an order nobody has touched shows one entry rather
    /// than seven imaginary ones.
    /// </summary>
    // ==================================================================
    //  WHO SEES WHAT
    // ==================================================================

    /// <summary>
    /// The user id a salesperson's view must be narrowed to, or null when the
    /// caller is entitled to the whole book.
    ///
    /// The rule, in the words it was asked for: a rep sees the orders they
    /// created and nothing else. Accounts and the Super Admin see everything,
    /// because you cannot reconcile a ledger through a keyhole.
    ///
    /// Note this is deliberately about the ROLE, not the permission. Giving
    /// Sales the right to handle returns opens the returns screen to them; it
    /// does not turn them into an accountant.
    /// </summary>
    private int? SalesScopeUserId() =>
        CurrentRole() == OrderWorkflow.RoleSales ? CurrentUserId() : null;

    /// <summary>
    /// May the caller open this invoice? Used by the endpoints that take an id
    /// straight off the URL -- a list that hides a row does not stop somebody
    /// asking for that row by number.
    /// </summary>
    private async Task<bool> MaySeeInvoice(int invoiceId)
    {
        var me = SalesScopeUserId();
        if (me is null) return true;

        return await _db.SalesInvoices.AsNoTracking().AnyAsync(i =>
            i.InvoiceId == invoiceId &&
            (i.CreatedByUserId == me ||
             (i.Order != null && i.Order.SalesPersonUserId == me)));
    }

    private static ObjectResult NotYours(string what) =>
        new ObjectResult(new { message = $"This {what} was not created by you." }) { StatusCode = 403 };

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrder(int id)
    {
        try
        {
            var me = SalesScopeUserId();
            if (me is not null && !await _db.SalesOrders.AsNoTracking().AnyAsync(x =>
                    x.OrderId == id && (x.SalesPersonUserId == me || x.CreatedByUserId == me)))
                return NotYours("order");

            var o = await _db.SalesOrders.AsNoTracking()
                .Where(x => x.OrderId == id)
                .Select(x => new
                {
                    id = x.OrderId,
                    orderNo = x.OrderNo,
                    customerId = x.CustomerUserId,
                    customerName = x.CustomerUser.LegalName,
                    customerCode = x.CustomerUser.PartyCode,
                    customerPhone = x.CustomerUser.User.Phone,
                    customerAltPhone = x.CustomerUser.AltPhone,
                    customerAddress = x.CustomerUser.AddressLine,
                    customerType = x.CustomerUser.Category.CategoryName,
                    city = x.CustomerUser.City.CityName,
                    creditLimit = x.CustomerUser.CreditLimit,
                    creditDays = x.CustomerUser.CreditDays,
                    holdPolicy = x.CustomerUser.HoldPolicy.PolicyKey,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    salesPerson = x.SalesPersonUser != null ? x.SalesPersonUser.User.FullName : null,
                    orderDate = x.OrderDate,
                    deliveryDate = x.DeliveryDate,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    total = x.TotalAmount,
                    methodId = x.MethodId,
                    paymentMethod = x.Method.MethodKey,
                    paymentMethodName = x.Method.MethodName,
                    creditHoldReason = x.CreditHoldReason,
                    notes = x.Notes,
                    createdBy = x.CreatedByUser.FullName,
                    createdAt = x.CreatedAt,

                    invoiceId = x.SalesInvoice != null ? (int?)x.SalesInvoice.InvoiceId : null,
                    invoiceNo = x.SalesInvoice != null ? x.SalesInvoice.InvoiceNo : null,
                    invoicePdfUrl = x.SalesInvoice != null ? x.SalesInvoice.PdfUrl : null,

                    paidAmount = x.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m,

                    outstanding = _db.JournalEntryLines
                        .Where(l => l.PartyUserId == x.CustomerUserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m,

                    channel = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Channel.ChannelKey).FirstOrDefault(),
                    carrier = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Courier != null ? d.Courier.CourierName : null).FirstOrDefault(),
                    trackingNo = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.TrackingNo).FirstOrDefault(),
                    deliveryState = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Status.StatusKey).FirstOrDefault(),
                    dispatchedOn = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.BookedDate).FirstOrDefault(),
                    deliveredOn = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.DeliveredDate).FirstOrDefault(),

                    lines = x.SalesOrderItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.OrderItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        name = i.Product.ProductName,
                        sku = i.Product.Sku,
                        packing = i.Product.Packing,
                        qty = i.Quantity,
                        rate = i.UnitPrice,
                        discountPercent = i.DiscountPercent,
                        taxPercent = i.TaxPercent,
                        lineTotal = i.LineTotal
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (o is null) return NotFound(new { message = $"No order with id {id}." });

            var activity = await _db.ActivityLogs.AsNoTracking()
                .Where(a => a.EntityReference == o.orderNo)
                .OrderBy(a => a.LoggedAt)
                .Select(a => new
                {
                    id = a.LogId,
                    action = a.ActionName,
                    entityType = a.EntityType,
                    detail = a.Detail,
                    at = a.LoggedAt,
                    severity = a.Severity.SeverityKey,
                    user = a.User != null ? a.User.FullName : "System"
                })
                .ToListAsync();

            return Ok(new
            {
                o.id, o.orderNo, o.customerId, o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerCode, o.customerPhone, o.customerAltPhone, o.customerAddress,
                o.customerType, o.city, o.creditLimit, o.creditDays, o.holdPolicy,
                o.locationId, o.location, o.salesPerson,
                o.orderDate, o.deliveryDate, o.status, o.statusName,
                o.subtotal, o.discount, o.tax, o.total,
                o.methodId, o.paymentMethod, o.paymentMethodName,
                o.creditHoldReason, o.notes, o.createdBy, o.createdAt,
                o.invoiceId, o.invoiceNo, o.invoicePdfUrl,
                /* The link the WhatsApp share should send. Derived, not stored,
                   so it is always right for the host answering this request. */
                invoiceShareUrl = o.invoiceNo == null ? null : ShareLink(o.invoiceNo),
                o.paidAmount,
                balance = o.total - o.paidAmount,
                paymentStatus = o.paidAmount <= 0 ? "UNPAID"
                              : o.paidAmount >= o.total ? "PAID" : "PARTIAL",
                o.outstanding,
                o.channel, o.carrier, o.trackingNo, o.deliveryState,
                o.dispatchedOn, o.deliveredOn,
                o.lines,
                activity
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {id}");
        }
    }

    /// <summary>
    /// Takes a customer order. The line totals are recomputed here from qty,
    /// rate, discount and tax -- never trusted from the browser, because a
    /// total that arrives from the client is a total anybody can edit.
    ///
    /// Three outcomes, and the screen is told which one it got:
    ///   SaveAsDraft   -> parked at DRAFT, no credit check, no invoice.
    ///   over limit    -> CREDIT_HOLD, and it lands on the Limit Alerts queue.
    ///   otherwise     -> SUBMITTED, and if RaiseInvoice is set the invoice is
    ///                    cut in the same transaction and the bill rendered.
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] OrderRequest body)
    {
        try
        {
            /* One validator, shared with UpdateOrder. Two copies of the same
               rules is two sets of rules the day somebody edits one. */
            var invalidOrder = await ValidateOrderRequest(body);
            if (invalidOrder is not null) return BadRequest(new { message = invalidOrder });

            /* The chain is: sales writes it, the Super Admin confirms it, and
               only then is it billed. A rep ticking "invoice it too" would step
               straight over the confirmation, so for that role the tick is
               ignored and the reply says so. The admin and the order desk can
               still bill on the spot, which is what the counter needs. */
            var raiseInvoice = body.RaiseInvoice && CurrentRole() != OrderWorkflow.RoleSales;
            var invoiceRefused = body.RaiseInvoice && !raiseInvoice;

            var (subtotal, discount, tax, total) = Totals(body.Lines);

            /* A draft is a scratch pad. It is not checked against the limit and
               it does not reserve anything, because half the drafts on this
               screen are abandoned. */
            if (body.SaveAsDraft)
            {
                var draftStatus = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
                if (draftStatus is null)
                    return BadRequest(new { message = "No DRAFT order status is configured." });

                var draft = await SaveOrder(body, draftStatus.StatusId, subtotal, discount, tax, total, null);

                await Log("ORDER_DRAFTED", "SalesOrder", draft.OrderNo,
                    $"{body.Lines.Count} lines, {total:N0}", 1);

                return Ok(new
                {
                    id = draft.OrderId,
                    orderNo = draft.OrderNo,
                    status = "DRAFT",
                    onCreditHold = false,
                    invoiceId = (int?)null,
                    invoiceNo = (string?)null,
                    invoicePdfUrl = (string?)null,
                    message = $"Draft {draft.OrderNo} saved. Nothing is committed until you submit it."
                });
            }

            /* Credit-hold decision. The rep cannot set a limit and cannot wave a
               breach through -- the order is saved ON HOLD and lands on the
               Super Admin dashboard for the owner to approve. */
            var party = await _db.Parties.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == body.CustomerId);
            var outstanding = await _db.JournalEntryLines
                .Where(l => l.PartyUserId == body.CustomerId && l.Entry.StatusId == 2)
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;

            var wouldBe = outstanding + total;
            var overLimit = party is not null && party.CreditLimit > 0 && wouldBe > party.CreditLimit;

            var holdStatus = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "CREDIT_HOLD");
            var newStatus = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "SUBMITTED");
            /* Real OrderStatus keys: DRAFT, SUBMITTED, CREDIT_HOLD, CONFIRMED,
               PROCESSING, PACKED, DISPATCHED, INVOICED, DELIVERED, CANCELLED,
               RETURNED. A new order is SUBMITTED -- there is no "NEW". */
            if (newStatus is null)
                return BadRequest(new { message = "No SUBMITTED order status is configured." });

            var statusId = overLimit && holdStatus is not null
                ? holdStatus.StatusId
                : newStatus.StatusId;

            var holdReason = overLimit
                ? $"Order takes the balance to {wouldBe:N0} against a limit of {party!.CreditLimit:N0}."
                : null;

            var order = await SaveOrder(body, statusId, subtotal, discount, tax, total, holdReason);

            /* Starts the six-hourly nudge running. The notification a few lines
               below is reminder number one; ConfirmReminderService picks it up
               from here. */
            if (!overLimit)
            {
                order.ConfirmRemindedAt = Now();
                await _db.SaveChangesAsync();
            }

            await Log(overLimit ? "ORDER_CREATED_ON_HOLD" : "ORDER_CREATED",
                "SalesOrder", order.OrderNo,
                $"{body.Lines.Count} lines, {total:N0}", overLimit ? 2 : 1);

            /* The invoice. An order that is on hold must not be billed -- that
               is the whole point of the hold -- so it is only cut when the
               order went through clean. */
            SalesInvoice? invoice = null;
            if (raiseInvoice && !overLimit)
            {
                invoice = await RaiseInvoiceForOrder(order, body.Lines, body.MethodId, body.DueDate);
                await Log("INVOICE_CREATED", "SalesInvoice", invoice.InvoiceNo,
                    $"raised with order {order.OrderNo}, {total:N0}", 2);
            }

            var bill = invoice is null ? null : await TryBuildBill(invoice.InvoiceId);

            /* ── A1 / B1 ──────────────────────────────────────────────────
               Fired last, after every database write has already succeeded,
               and it cannot fail the request -- see PushNotificationService.

               An order held on a credit limit is a different message to a
               different audience: it is the one place in this whole flow where
               money is stuck, so it goes to Accounts as well and it is the kind
               that buzzes a phone. */
            var customerName = await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == order.CustomerUserId)
                .Select(pa => pa.LegalName).FirstOrDefaultAsync() ?? "a customer";
            var takenBy = CurrentUserName();

            if (overLimit)
            {
                await _push.NotifyRolesAsync(
                    new[] { "super-admin", "accountant", "sales" },
                    NotificationKinds.CreditHold,
                    $"Order held by {takenBy}",
                    $"{order.OrderNo} is stuck -- {customerName} is over their credit limit. " +
                    $"{order.TotalAmount:N0} cannot move until somebody clears it.",
                    url: $"/sales/credit-holds",
                    severe: true);
            }
            else
            {
                await _push.NotifyRolesAsync(
                    new[] { "super-admin", "order-dept" },
                    NotificationKinds.OrderCreated,
                    $"Order created by {takenBy}",
                    $"{order.OrderNo} -- {customerName}, PKR {order.TotalAmount:N0}.",
                    url: $"/sales/orders/{order.OrderId}",
                    exceptUserId: CurrentUserId());
            }

            if (invoice is not null)
            {
                await _push.NotifyRolesAsync(
                    new[] { "super-admin", "accountant" },
                    NotificationKinds.InvoiceRaised,
                    $"Invoice raised by {takenBy}",
                    $"{invoice.InvoiceNo} -- {customerName}, PKR {invoice.TotalAmount:N0}, for {order.OrderNo}.",
                    url: $"/sales/invoices/{invoice.InvoiceId}",
                    exceptUserId: CurrentUserId());
            }

            return Ok(new
            {
                id = order.OrderId,
                orderNo = order.OrderNo,
                status = overLimit ? "CREDIT_HOLD" : "SUBMITTED",
                onCreditHold = overLimit,
                invoiceId = invoice?.InvoiceId,
                invoiceNo = invoice?.InvoiceNo,
                invoicePdfUrl = bill?.PdfUrl,
                invoiceShareUrl = bill?.ShareUrl,
                message = overLimit
                    ? $"Order {order.OrderNo} saved on credit hold -- it needs the owner's approval."
                    : invoice is not null
                        ? $"Order {order.OrderNo} saved and invoiced as {invoice.InvoiceNo}."
                        : invoiceRefused
                            ? $"Order {order.OrderNo} submitted. It can be invoiced once the owner confirms it."
                            : $"Order {order.OrderNo} saved."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the order");
        }
    }

    /// <summary>
    /// Writes the order header and its lines inside one transaction. Shared by
    /// the draft path and the live path so the two can never drift.
    /// </summary>
    /// <summary>
    /// The checks an order has to pass, whether it is being created or edited.
    /// Returns the message to refuse with, or null when it is sound.
    /// </summary>
    private async Task<string?> ValidateOrderRequest(OrderRequest body)
    {
        if (body.Lines is null || body.Lines.Count == 0)
            return "An order needs at least one line.";
        if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerId))
            return "Pick a valid customer.";
        if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
            return "Pick a valid location.";
        if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.MethodId))
            return "Pick a valid payment method.";

        foreach (var l in body.Lines)
        {
            if (l.Qty <= 0) return "Every line needs a quantity above zero.";
            if (l.Rate < 0) return "A rate cannot be negative.";
            if (l.DiscountPercent is < 0 or > 100)
                return "A line discount must be between 0 and 100 percent.";
            if (l.TaxPercent is < 0 or > 100)
                return "A line tax rate must be between 0 and 100 percent.";
            if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                return $"Product {l.ProductId} does not exist.";
        }

        return null;
    }

    private async Task<SalesOrder> SaveOrder(OrderRequest body, int statusId,
        decimal subtotal, decimal discount, decimal tax, decimal total, string? holdReason)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();

        var order = new SalesOrder
        {
            /* "ORD", not "SO" -- see the class comment. */
            OrderNo = await NextNumber("ORD"),
            CustomerUserId = body.CustomerId,
            LocationId = body.LocationId,
            SalesPersonUserId = body.SalesPersonUserId ?? await CurrentEmployeeId(),
            OrderDate = body.OrderDate ?? Today(),
            DeliveryDate = body.DeliveryDate,
            StatusId = statusId,
            MethodId = body.MethodId,
            Subtotal = subtotal,
            DiscountAmount = discount,
            TaxAmount = tax,
            TotalAmount = total,
            CreditHoldReason = holdReason,
            Notes = body.Notes,
            CreatedByUserId = CurrentUserId(),
            CreatedAt = Today()
        };
        _db.SalesOrders.Add(order);
        await _db.SaveChangesAsync();

        short n = 1;
        foreach (var l in body.Lines)
        {
            var gross = l.Qty * l.Rate;
            var disc = gross * (l.DiscountPercent / 100m);
            var net = gross - disc;
            _db.SalesOrderItems.Add(new SalesOrderItem
            {
                OrderId = order.OrderId,
                LineNo = n++,
                ProductId = l.ProductId,
                Quantity = l.Qty,
                UnitPrice = l.Rate,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                LineTotal = Money(net + net * (l.TaxPercent / 100m))
            });
        }
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return order;
    }

    /// <summary>
    /// Cuts the invoice for an order and moves the order to INVOICED. UnitCost
    /// is captured per line at invoice time: the margin reports need what the
    /// item cost THAT DAY, and Product.CostPrice moves.
    /// </summary>
    private async Task<SalesInvoice> RaiseInvoiceForOrder(
        SalesOrder order, IReadOnlyList<OrderLineRequest> lines, int methodId, DateOnly? dueDate)
    {
        var status = await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED")
                     ?? await _db.InvoiceStatuses.FirstAsync(s => s.StatusKey == "DRAFT");

        await using var tx = await _db.Database.BeginTransactionAsync();

        var inv = new SalesInvoice
        {
            InvoiceNo = await NextNumber("INV"),
            OrderId = order.OrderId,
            CustomerUserId = order.CustomerUserId,
            LocationId = order.LocationId,
            InvoiceDate = order.OrderDate,
            DueDate = dueDate ?? order.OrderDate.AddDays(30),
            Subtotal = order.Subtotal,
            DiscountAmount = order.DiscountAmount,
            TaxAmount = order.TaxAmount,
            TotalAmount = order.TotalAmount,
            StatusId = status.StatusId,
            MethodId = methodId,
            CreatedByUserId = CurrentUserId()
        };
        _db.SalesInvoices.Add(inv);
        await _db.SaveChangesAsync();

        short n = 1;
        foreach (var l in lines)
        {
            var gross = l.Qty * l.Rate;
            var disc = gross * (l.DiscountPercent / 100m);
            var net = gross - disc;
            var cost = await _db.Products.Where(p => p.ProductId == l.ProductId)
                .Select(p => p.CostPrice).FirstAsync();

            _db.SalesInvoiceItems.Add(new SalesInvoiceItem
            {
                InvoiceId = inv.InvoiceId,
                LineNo = n++,
                ProductId = l.ProductId,
                Quantity = l.Qty,
                UnitPrice = l.Rate,
                DiscountPercent = l.DiscountPercent,
                TaxPercent = l.TaxPercent,
                UnitCost = cost,
                LineTotal = Money(net + net * (l.TaxPercent / 100m))
            });
        }

        var invoiced = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "INVOICED");
        var live = await _db.SalesOrders.FirstAsync(o => o.OrderId == order.OrderId);
        if (invoiced is not null) live.StatusId = invoiced.StatusId;

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return inv;
    }

    /// <summary>
    /// Invoices an order that already exists -- the button on the order detail
    /// screen. Separate from CreateOrder because by this point the lines are
    /// whatever is in the database, not whatever the browser is holding.
    /// </summary>
    [HttpPost("orders/{id:int}/invoice")]
    [Authorize(Policy = "perm:invoices.create")]
    public async Task<IActionResult> InvoiceOrder(int id, [FromBody] InvoiceOrderRequest? body)
    {
        try
        {
            var order = await _db.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var existing = await _db.SalesInvoices.AsNoTracking()
                .FirstOrDefaultAsync(i => i.OrderId == id);
            if (existing is not null)
                return BadRequest(new { message = $"Order {order.OrderNo} is already invoiced as {existing.InvoiceNo}." });

            var statusKey = await _db.OrderStatuses.Where(s => s.StatusId == order.StatusId)
                .Select(s => s.StatusKey).FirstAsync();
            if (statusKey is "DRAFT" or "CREDIT_HOLD" or "CANCELLED")
                return BadRequest(new
                {
                    message = $"An order that is {statusKey.Replace('_', ' ').ToLowerInvariant()} cannot be invoiced."
                });

            var lines = await _db.SalesOrderItems.AsNoTracking()
                .Where(i => i.OrderId == id).OrderBy(i => i.LineNo)
                .Select(i => new OrderLineRequest(
                    i.ProductId, i.Quantity, i.UnitPrice, i.DiscountPercent, i.TaxPercent))
                .ToListAsync();

            if (lines.Count == 0)
                return BadRequest(new { message = "That order has no lines to invoice." });

            var inv = await RaiseInvoiceForOrder(order, lines, body?.MethodId ?? order.MethodId, body?.DueDate);
            await Log("INVOICE_CREATED", "SalesInvoice", inv.InvoiceNo,
                $"raised against {order.OrderNo}, {inv.TotalAmount:N0}", 2);
            await Log("ORDER_INVOICED", "SalesOrder", order.OrderNo, inv.InvoiceNo, 1);

            var bill = await TryBuildBill(inv.InvoiceId);

            /* ── B3 ─────────────────────────────────────────────────────── */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.InvoiceRaised,
                $"Invoice raised by {CurrentUserName()}",
                $"{inv.InvoiceNo} -- PKR {inv.TotalAmount:N0}, against {order.OrderNo}.",
                url: $"/sales/invoices/{inv.InvoiceId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null
                    ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                invoiceId = inv.InvoiceId,
                invoiceNo = inv.InvoiceNo,
                invoicePdfUrl = bill?.PdfUrl,
                invoiceShareUrl = bill?.ShareUrl,
                message = $"Invoice {inv.InvoiceNo} raised against {order.OrderNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"invoice order {id}");
        }
    }

    /// <summary>
    /// Moves an order along the pipeline. The reason, when one is given, is
    /// written to the activity trail -- a cancellation with no recorded reason
    /// is the kind of thing that gets argued about a month later.
    /// </summary>
    /// <summary>
    /// Move an order along the chain -- or, for the Super Admin, anywhere at all.
    ///
    /// Every rule about who may make which move lives in
    /// Services/OrderWorkflow.cs. This action asks that class and does what it
    /// says; it does not carry a second copy of the rules.
    /// </summary>
    // ══════════════════════════════════════════════════════════════════
    //  EDITING AND DELETING AN ORDER
    // ══════════════════════════════════════════════════════════════════

    /*  Only the Super Admin may do either.
        A salesperson who needs an order changed files a request saying which
        order and why; the admin approves it from their dashboard, and that
        approval is a ONE-SHOT key for that one change. See OrderChangeRequest.

        Editing an order that has already been invoiced RE-WRITES THE INVOICE to
        match, including its stored PDF. An invoice that disagrees with the order
        it was raised from is worse than either of them being wrong on its own. */

    /// <summary>Replace an order's lines and details.</summary>
    [HttpPut("orders/{id:int}")]
    public async Task<IActionResult> UpdateOrder(int id, [FromBody] OrderRequest body)
    {
        try
        {
            var order = await _db.SalesOrders
                .Include(o => o.SalesOrderItems)
                /* Status is read a few lines down to refuse a delivered or
                   cancelled order. Without this Include it is null and the
                   check throws instead of refusing. */
                .Include(o => o.Status)
                .FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var role = CurrentRole();
            OrderChangeRequest? key = null;

            if (role != OrderWorkflow.RoleAdmin)
            {
                /* Not an admin -- so this needs an approved request, and it has
                   to belong to this person and this order. */
                key = await _db.OrderChangeRequests
                    .FirstOrDefaultAsync(r => r.OrderId == id
                                           && r.RequestedByUserId == CurrentUserId()
                                           && r.Kind == "EDIT"
                                           && r.Status == "APPROVED");
                if (key is null)
                    return StatusCode(403, new
                    {
                        message = "Only the Super Admin can edit an order. "
                                + "Ask for permission from the order screen and try again once it is approved."
                    });
            }

            if (order.Status.StatusKey is OrderWorkflow.Delivered or OrderWorkflow.Cancelled)
                return BadRequest(new
                {
                    message = $"{order.OrderNo} is {order.Status.StatusName.ToLowerInvariant()} and cannot be edited."
                });

            var invalid = await ValidateOrderRequest(body);
            if (invalid is not null) return BadRequest(new { message = invalid });

            var (subtotal, discount, tax, total) = Totals(body.Lines);

            await using var tx = await _db.Database.BeginTransactionAsync();

            order.CustomerUserId = body.CustomerId;
            order.LocationId = body.LocationId;
            order.DeliveryDate = body.DeliveryDate;
            order.MethodId = body.MethodId;
            order.Notes = body.Notes;
            order.Subtotal = subtotal;
            order.DiscountAmount = discount;
            order.TaxAmount = tax;
            order.TotalAmount = total;
            /* OrderDate is deliberately NOT moved. When a sale happened is a
               fact about the business, not a field to tidy up later. */

            _db.SalesOrderItems.RemoveRange(order.SalesOrderItems);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                _db.SalesOrderItems.Add(new SalesOrderItem
                {
                    OrderId = order.OrderId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitPrice = l.Rate,
                    DiscountPercent = l.DiscountPercent,
                    TaxPercent = l.TaxPercent,
                    LineTotal = Money(net + net * (l.TaxPercent / 100m))
                });
            }

            /* The invoice follows the order. */
            var invoice = await _db.SalesInvoices
                .Include(i => i.SalesInvoiceItems)
                .FirstOrDefaultAsync(i => i.OrderId == id);

            if (invoice is not null)
            {
                invoice.CustomerUserId = body.CustomerId;
                invoice.LocationId = body.LocationId;
                invoice.MethodId = body.MethodId;
                invoice.Subtotal = subtotal;
                invoice.DiscountAmount = discount;
                invoice.TaxAmount = tax;
                invoice.TotalAmount = total;

                _db.SalesInvoiceItems.RemoveRange(invoice.SalesInvoiceItems);
                await _db.SaveChangesAsync();

                short m = 1;
                foreach (var l in body.Lines)
                {
                    var gross = l.Qty * l.Rate;
                    var disc = gross * (l.DiscountPercent / 100m);
                    var net = gross - disc;
                    _db.SalesInvoiceItems.Add(new SalesInvoiceItem
                    {
                        InvoiceId = invoice.InvoiceId,
                        LineNo = m++,
                        ProductId = l.ProductId,
                        Quantity = l.Qty,
                        UnitPrice = l.Rate,
                        DiscountPercent = l.DiscountPercent,
                        TaxPercent = l.TaxPercent,
                        LineTotal = Money(net + net * (l.TaxPercent / 100m))
                    });
                }
            }

            if (key is not null) key.Status = "USED";   // the one-shot is spent

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("ORDER_UPDATED", "SalesOrder", order.OrderNo,
                $"{body.Lines.Count} lines, {total:N0}" + (invoice is null ? "" : $"; invoice {invoice.InvoiceNo} rebuilt"), 2);

            /* Rebuild the stored PDFs so the printed document matches. Swallowed
               on failure, as everywhere else -- the edit is saved either way. */
            await DocumentArchive.TryStoreForAsync(_db, _cfg, _logger, "sales-order", order.OrderId, CurrentUserId());
            if (invoice is not null)
            {
                invoice.PdfUrl = null;      // force a fresh render
                invoice.PdfPublicId = null;
                await _db.SaveChangesAsync();
                await TryBuildBill(invoice.InvoiceId);
            }

            var custName = await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == order.CustomerUserId)
                .Select(pa => pa.LegalName).FirstOrDefaultAsync() ?? "a customer";

            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.OrderCreated,
                $"Order edited by {CurrentUserName()}",
                $"{order.OrderNo} -- {custName} was changed to PKR {total:N0}"
                    + (invoice is null ? "." : $", and {invoice.InvoiceNo} was rebuilt to match."),
                url: $"/sales/orders/{order.OrderId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                id = order.OrderId,
                orderNo = order.OrderNo,
                invoiceRebuilt = invoice?.InvoiceNo,
                message = invoice is null
                    ? $"{order.OrderNo} updated."
                    : $"{order.OrderNo} updated, and {invoice.InvoiceNo} was rebuilt to match."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update order {id}");
        }
    }

    /// <summary>Delete an order outright. Super Admin, or an approved request.</summary>
    [HttpDelete("orders/{id:int}")]
    public async Task<IActionResult> DeleteOrder(int id)
    {
        try
        {
            var order = await _db.SalesOrders
                .Include(o => o.SalesOrderItems)
                .FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var role = CurrentRole();
            OrderChangeRequest? key = null;

            if (role != OrderWorkflow.RoleAdmin)
            {
                key = await _db.OrderChangeRequests
                    .FirstOrDefaultAsync(r => r.OrderId == id
                                           && r.RequestedByUserId == CurrentUserId()
                                           && r.Kind == "DELETE"
                                           && r.Status == "APPROVED");
                if (key is null)
                    return StatusCode(403, new
                    {
                        message = "Only the Super Admin can delete an order. "
                                + "Ask for permission from the order screen and try again once it is approved."
                    });
            }

            /* An invoiced order is money that has been billed. Deleting it would
               leave an invoice pointing at nothing, so it is refused outright --
               even for the admin. A sales return is the way to undo a sale. */
            var invoiceNo = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.OrderId == id).Select(i => i.InvoiceNo).FirstOrDefaultAsync();
            if (invoiceNo is not null)
                return BadRequest(new
                {
                    message = $"{order.OrderNo} has been invoiced as {invoiceNo}. "
                            + "Raise a sales return instead of deleting it."
                });

            var no = order.OrderNo;
            var custName = await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == order.CustomerUserId)
                .Select(pa => pa.LegalName).FirstOrDefaultAsync() ?? "a customer";
            var repId = order.SalesPersonUserId;

            await using var tx = await _db.Database.BeginTransactionAsync();

            _db.SalesOrderItems.RemoveRange(order.SalesOrderItems);
            if (key is not null) key.Status = "USED";
            await _db.SaveChangesAsync();

            _db.SalesOrders.Remove(order);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("ORDER_DELETED", "SalesOrder", no, $"{custName}", 3);

            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "order-dept" },
                NotificationKinds.OrderCreated,
                $"Order deleted by {CurrentUserName()}",
                $"{no} -- {custName} was deleted.",
                url: "/sales/orders",
                severe: true,
                exceptUserId: CurrentUserId(),
                alsoUserIds: repId is null ? null : new[] { repId.Value });

            return Ok(new { message = $"{no} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"delete order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ASKING PERMISSION TO EDIT OR DELETE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>File a request. Sales cannot edit or delete; they ask.</summary>
    [HttpPost("orders/{id:int}/change-request")]
    public async Task<IActionResult> RequestOrderChange(int id, [FromBody] ChangeRequestBody body)
    {
        try
        {
            var kind = (body?.Kind ?? "").Trim().ToUpperInvariant();
            if (kind is not ("EDIT" or "DELETE"))
                return BadRequest(new { message = "Ask to EDIT or to DELETE." });
            if (string.IsNullOrWhiteSpace(body!.Reason) || body.Reason.Trim().Length < 5)
                return BadRequest(new { message = "Give a reason -- the admin has to decide on it." });

            var order = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == id)
                .Select(o => new { o.OrderNo, o.SalesPersonUserId, o.CustomerUser.LegalName, o.TotalAmount })
                .FirstOrDefaultAsync();
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            if (CurrentRole() == OrderWorkflow.RoleSales && order.SalesPersonUserId != CurrentUserId())
                return StatusCode(403, new { message = "This is not your order." });

            var already = await _db.OrderChangeRequests
                .AnyAsync(r => r.OrderId == id && r.RequestedByUserId == CurrentUserId()
                            && r.Kind == kind && r.Status == "PENDING");
            if (already)
                return BadRequest(new { message = "You have already asked. The admin has it on their dashboard." });

            _db.OrderChangeRequests.Add(new OrderChangeRequest
            {
                OrderId = id,
                RequestedByUserId = CurrentUserId(),
                Kind = kind,
                Reason = body.Reason.Trim(),
                Status = "PENDING",
                CreatedAt = Now()
            });
            await _db.SaveChangesAsync();

            await Log("ORDER_CHANGE_REQUESTED", "SalesOrder", order.OrderNo,
                $"{kind}: {body.Reason.Trim()}", 2);

            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.OrderCreated,
                $"Permission asked by {CurrentUserName()}",
                $"{CurrentUserName(false)} wants to {kind.ToLowerInvariant()} {order.OrderNo} "
                    + $"({order.LegalName}, PKR {order.TotalAmount:N0}). Reason: {body.Reason.Trim()}",
                url: "/dashboard",
                severe: true);

            return Ok(new { message = "Asked. The admin will see it on their dashboard." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "ask for permission to change the order");
        }
    }

    /// <summary>Requests waiting on the admin. Shown on their dashboard.</summary>
    [HttpGet("order-change-requests")]
    public async Task<IActionResult> GetOrderChangeRequests([FromQuery] string status = "PENDING")
    {
        try
        {
            if (CurrentRole() != OrderWorkflow.RoleAdmin)
                return StatusCode(403, new { message = "Only the Super Admin sees these." });

            var rows = await _db.OrderChangeRequests.AsNoTracking()
                .Where(r => r.Status == status)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new
                {
                    id = r.RequestId,
                    orderId = r.OrderId,
                    orderNo = r.Order.OrderNo,
                    customer = r.Order.CustomerUser.LegalName,
                    total = r.Order.TotalAmount,
                    orderStatus = r.Order.Status.StatusName,
                    kind = r.Kind,
                    reason = r.Reason,
                    askedBy = r.RequestedByUser.FullName,
                    askedAt = r.CreatedAt
                })
                .ToListAsync();

            return Ok(new { count = rows.Count, items = rows });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the change requests");
        }
    }

    /// <summary>Approve with a tick, refuse with a cross.</summary>
    [HttpPost("order-change-requests/{requestId:int}/decide")]
    public async Task<IActionResult> DecideOrderChange(int requestId, [FromBody] DecideChangeBody body)
    {
        try
        {
            if (CurrentRole() != OrderWorkflow.RoleAdmin)
                return StatusCode(403, new { message = "Only the Super Admin can decide these." });

            var approve = body?.Approve ?? false;

            var req = await _db.OrderChangeRequests
                .Include(r => r.Order)
                .FirstOrDefaultAsync(r => r.RequestId == requestId);
            if (req is null) return NotFound(new { message = "No such request." });
            if (req.Status != "PENDING")
                return BadRequest(new { message = $"That request has already been {req.Status.ToLowerInvariant()}." });

            req.Status = approve ? "APPROVED" : "DECLINED";
            req.DecidedByUserId = CurrentUserId();
            req.DecidedAt = Now();
            req.DecisionNote = body?.Note?.Trim();
            await _db.SaveChangesAsync();

            await Log(approve ? "ORDER_CHANGE_APPROVED" : "ORDER_CHANGE_DECLINED",
                "SalesOrder", req.Order.OrderNo, $"{req.Kind}. {body?.Note?.Trim()}", 2);

            await _push.NotifyAsync(
                req.RequestedByUserId,
                NotificationKinds.OrderCreated,
                approve ? $"Permission granted by {CurrentUserName()}" : $"Permission refused by {CurrentUserName()}",
                approve
                    ? $"You can now {req.Kind.ToLowerInvariant()} {req.Order.OrderNo}. It is a one-time permission."
                    : $"Your request to {req.Kind.ToLowerInvariant()} {req.Order.OrderNo} was refused."
                        + (string.IsNullOrWhiteSpace(body?.Note) ? "" : $" {body!.Note!.Trim()}"),
                url: $"/sales/orders/{req.OrderId}",
                severe: true);

            return Ok(new
            {
                id = requestId,
                status = req.Status,
                message = approve ? "Permission granted." : "Request refused."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "decide the change request");
        }
    }

    /// <summary>
    /// What this person may currently do to this order -- used by the screen to
    /// decide between showing Edit, or showing "Ask for permission".
    /// </summary>
    [HttpGet("orders/{id:int}/my-permissions")]
    public async Task<IActionResult> GetMyOrderPermissions(int id)
    {
        try
        {
            var role = CurrentRole();
            var me = CurrentUserId();

            var order = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == id)
                .Select(o => new { o.SalesPersonUserId, o.Status.StatusKey })
                .FirstOrDefaultAsync();
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var isAdmin = role == OrderWorkflow.RoleAdmin;
            var mine = role != OrderWorkflow.RoleSales || order.SalesPersonUserId == me;

            var open = await _db.OrderChangeRequests.AsNoTracking()
                .Where(r => r.OrderId == id && r.RequestedByUserId == me
                         && (r.Status == "PENDING" || r.Status == "APPROVED"))
                .Select(r => new { r.Kind, r.Status })
                .ToListAsync();

            bool Granted(string k) => open.Any(r => r.Kind == k && r.Status == "APPROVED");
            bool Asked(string k) => open.Any(r => r.Kind == k && r.Status == "PENDING");

            var invoiced = await _db.SalesInvoices.AnyAsync(i => i.OrderId == id);

            return Ok(new
            {
                isAdmin,
                isMine = mine,
                canEdit = isAdmin || Granted("EDIT"),
                canDelete = (isAdmin || Granted("DELETE")) && !invoiced,
                editRequested = Asked("EDIT"),
                deleteRequested = Asked("DELETE"),
                canAsk = !isAdmin && mine,
                invoiced
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"read your permissions on order {id}");
        }
    }

    [HttpPatch("orders/{id:int}/status")]
    public async Task<IActionResult> SetOrderStatus(int id, [FromBody] StatusRequest body)
    {
        try
        {
            var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var status = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == body.StatusKey);
            if (status is null) return BadRequest(new { message = $"Unknown status '{body.StatusKey}'." });

            var current = await _db.OrderStatuses.AsNoTracking()
                .FirstAsync(s => s.StatusId == order.StatusId);

            var role = CurrentRole();

            /* A salesperson only ever works on their own orders. Checked here as
               well as in the list query, because a list that hides a row does
               not stop somebody calling the endpoint with its id. */
            if (role == OrderWorkflow.RoleSales && order.SalesPersonUserId != CurrentUserId())
                return StatusCode(403, new { message = "This is not your order." });

            if (!OrderWorkflow.CanMove(role, current.StatusKey, status.StatusKey))
                return StatusCode(403, new
                {
                    message = current.StatusKey == status.StatusKey
                        ? $"{order.OrderNo} is already {current.StatusName.ToLowerInvariant()}."
                        : $"Your role cannot move an order from {current.StatusName.ToLowerInvariant()} " +
                          $"to {status.StatusName.ToLowerInvariant()}."
                });

            if (status.StatusKey == OrderWorkflow.Declined && string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "Declining an order needs a reason." });

            if (body.StatusKey == OrderWorkflow.Cancelled &&
                await _db.SalesInvoices.AnyAsync(i => i.OrderId == id))
                return BadRequest(new
                {
                    message = "This order has already been invoiced. Raise a sales return instead of cancelling it."
                });

            order.StatusId = status.StatusId;
            if (body.StatusKey != OrderWorkflow.CreditHold) order.CreditHoldReason = null;

            /* Leaving SUBMITTED means the decision has been made, so the
               six-hourly nudge has nothing left to chase. Arriving at it starts
               that clock, with this move's own notification as the first
               reminder -- see ConfirmReminderService. */
            if (current.StatusKey == OrderWorkflow.Submitted) order.ConfirmRemindedAt = null;
            if (status.StatusKey == OrderWorkflow.Submitted) order.ConfirmRemindedAt = Now();

            await _db.SaveChangesAsync();

            var detail = string.IsNullOrWhiteSpace(body.Reason)
                ? $"{current.StatusName} -> {status.StatusName}"
                : $"{current.StatusName} -> {status.StatusName}. {body.Reason.Trim()}";

            await Log(body.StatusKey == OrderWorkflow.Cancelled ? "ORDER_CANCELLED" : "ORDER_STATUS_CHANGED",
                "SalesOrder", order.OrderNo, detail,
                body.StatusKey is OrderWorkflow.Cancelled or OrderWorkflow.Declined ? 2 : 1);

            /* Who hears about this step, and in what words, is also the
               workflow's business rather than a switch statement here. */
            var custName = await _db.Parties.AsNoTracking()
                .Where(pa => pa.UserId == order.CustomerUserId)
                .Select(pa => pa.LegalName).FirstOrDefaultAsync() ?? "a customer";

            var (kind, roles, purpose, line) = OrderWorkflow.Announcement(
                status.StatusKey, order.OrderNo, custName, CurrentUserName());

            if (kind.Length > 0)
            {
                await _push.NotifyRolesAsync(
                    roles, kind, purpose,
                    string.IsNullOrWhiteSpace(body.Reason) ? line : $"{line} {body.Reason.Trim()}",
                    url: $"/sales/orders/{order.OrderId}",
                    severe: status.StatusKey == OrderWorkflow.Declined,
                    exceptUserId: CurrentUserId(),
                    alsoUserIds: order.SalesPersonUserId is null
                        ? null : new[] { order.SalesPersonUserId.Value });
            }

            return Ok(new
            {
                id,
                status = status.StatusKey,
                statusName = status.StatusName,
                step = OrderWorkflow.Step(status.StatusKey),
                nextForMe = OrderWorkflow.NextFor(role, status.StatusKey),
                message = $"{order.OrderNo} is now {status.StatusName.ToLowerInvariant()}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE WAREHOUSE KEEPER'S QUEUE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every order the warehouse has to pick, with the items on it.
    ///
    /// The keeper's whole job is one step of the chain: an order the owner has
    /// confirmed becomes stock on a trolley, and then it is on its way to the
    /// order department. So this is deliberately not a filter on the orders
    /// screen -- it is the queue, with the picking list already open, because
    /// somebody standing at a shelf should not have to click into nine orders
    /// to find out what to pull off it.
    ///
    /// CONFIRMED and INVOICED both qualify. Whether the invoice has been cut
    /// yet is an office question; the goods are the same goods either way, and
    /// making the floor wait on paperwork is how orders sit for a day.
    /// </summary>
    [HttpGet("warehouse/queue")]
    [Authorize(Policy = "perm:orders.warehouse")]
    public async Task<IActionResult> GetWarehouseQueue([FromQuery] int? locationId)
    {
        try
        {
            var ready = new[] { OrderWorkflow.Confirmed, OrderWorkflow.Invoiced };

            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => ready.Contains(o.Status.StatusKey));

            if (locationId is not null)
                rows = rows.Where(o => o.LocationId == locationId);

            var items = await rows
                .OrderBy(o => o.OrderDate).ThenBy(o => o.OrderId)
                .Take(100)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerName = o.CustomerUser.LegalName,
                    city = o.CustomerUser.City.CityName,
                    locationId = o.LocationId,
                    location = o.Location.LocationName,
                    orderDate = o.OrderDate,
                    deliveryDate = o.DeliveryDate,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    total = o.TotalAmount,
                    salesPerson = o.CreatedByUser.FullName,
                    invoiceNo = _db.SalesInvoices
                        .Where(i => i.OrderId == o.OrderId)
                        .Select(i => i.InvoiceNo).FirstOrDefault(),
                    lines = o.SalesOrderItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        packing = l.Product.Packing,
                        qty = l.Quantity,
                        /* What is actually on the shelf at the branch the order
                           is being served from. A picking list without this is
                           a list of disappointments. */
                        onHand = _db.StockBalances
                            .Where(b => b.ProductId == l.ProductId && b.LocationId == o.LocationId)
                            .Select(b => (int?)b.Quantity).FirstOrDefault() ?? 0
                    }).ToList()
                })
                .ToListAsync();

            return Ok(new
            {
                count = items.Count,
                units = items.Sum(o => o.lines.Sum(l => l.qty)),
                /* Short is the whole point of the screen: these are the orders
                   the keeper cannot complete without moving stock first. */
                short_ = items.Count(o => o.lines.Any(l => l.onHand < l.qty)),
                items
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the warehouse queue");
        }
    }

    /// <summary>
    /// The whole chain, and what this person may do with the order they are
    /// looking at. The screen builds its dropdown and its one-click button from
    /// this rather than deciding for itself what a role is allowed to do.
    /// </summary>
    [HttpGet("orders/{id:int}/workflow")]
    public async Task<IActionResult> GetOrderWorkflow(int id)
    {
        try
        {
            var order = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == id)
                .Select(o => new { o.OrderId, o.SalesPersonUserId, o.Status.StatusKey })
                .FirstOrDefaultAsync();
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var role = CurrentRole();
            var mine = role != OrderWorkflow.RoleSales || order.SalesPersonUserId == CurrentUserId();

            var names = await _db.OrderStatuses.AsNoTracking()
                .OrderBy(s => s.SortOrder)
                .Select(s => new { s.StatusKey, s.StatusName, s.SortOrder })
                .ToListAsync();

            var allowed = mine ? OrderWorkflow.AllowedTargets(role, order.StatusKey) : new List<string>();

            return Ok(new
            {
                current = order.StatusKey,
                step = OrderWorkflow.Step(order.StatusKey),
                chain = OrderWorkflow.Chain
                    .Select((k, i) => new
                    {
                        step = i + 1,
                        key = k,
                        name = names.FirstOrDefault(n => n.StatusKey == k)?.StatusName ?? k
                    }),
                /* One-click. Null when there is nothing obvious to do next. */
                next = mine ? OrderWorkflow.NextFor(role, order.StatusKey) : null,
                nextName = mine && OrderWorkflow.NextFor(role, order.StatusKey) is string nk
                    ? names.FirstOrDefault(n => n.StatusKey == nk)?.StatusName ?? nk
                    : null,
                /* Everything the dropdown may offer. Only the Super Admin gets
                   the whole chain; everyone else gets their own one or two. */
                allowed = allowed.Select(k => new
                {
                    key = k,
                    name = names.FirstOrDefault(n => n.StatusKey == k)?.StatusName ?? k,
                    step = OrderWorkflow.Step(k)
                }),
                canSetAnything = role == OrderWorkflow.RoleAdmin,
                isMine = mine
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"read the workflow for order {id}");
        }
    }

    /// <summary>
    /// The queue of orders parked over their credit limit. Accountant and owner
    /// only -- a sales rep must not see, let alone clear, this list.
    ///
    /// The customer's phone comes back too: the Remind button on that screen
    /// opens WhatsApp on the buyer's own number, and a reminder sent to a
    /// number typed from memory is worse than no reminder.
    /// </summary>
    [HttpGet("credit-holds")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> GetCreditHolds()
    {
        try
        {
            var rows = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey == "CREDIT_HOLD")
                .OrderBy(o => o.OrderDate)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = o.CustomerUser.LegalName,
                    customerCode = o.CustomerUser.PartyCode,
                    customerPhone = o.CustomerUser.User.Phone,
                    customerAltPhone = o.CustomerUser.AltPhone,
                    city = o.CustomerUser.City.CityName,
                    creditLimit = o.CustomerUser.CreditLimit,
                    creditDays = o.CustomerUser.CreditDays,
                    holdPolicy = o.CustomerUser.HoldPolicy.PolicyKey,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    reason = o.CreditHoldReason,
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : null,
                    itemCount = o.SalesOrderItems.Count,
                    outstanding = _db.JournalEntryLines
                        .Where(l => l.PartyUserId == o.CustomerUserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m,
                    paidAmount = o.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            return Ok(rows.Select(r => new
            {
                r.id, r.orderNo, r.customerId, r.customerName, r.customerCode,
                customerInitials = Initials(r.customerName),
                r.customerPhone, r.customerAltPhone, r.city,
                r.creditLimit, r.creditDays, r.holdPolicy, r.orderDate,
                r.total, r.reason, r.salesPerson, r.itemCount,
                r.outstanding, r.paidAmount,
                overBy = r.creditLimit > 0 ? Math.Max(0, r.outstanding + r.total - r.creditLimit) : 0m
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the credit-hold queue");
        }
    }

    /// <summary>
    /// Just the number, for the badge next to "Limit Alerts" in the sidebar.
    ///
    /// Open to any signed-in member of staff on purpose: this returns a count
    /// and nothing else. The list itself stays behind the Accountant policy,
    /// and the sidebar only draws the badge for somebody who holds
    /// limits.manage anyway.
    /// </summary>
    [HttpGet("credit-holds/count")]
    public async Task<IActionResult> GetCreditHoldCount()
    {
        try
        {
            return Ok(new
            {
                count = await _db.SalesOrders.CountAsync(o => o.Status.StatusKey == "CREDIT_HOLD")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "count the credit holds");
        }
    }

    /// <summary>
    /// Lets the order through despite the limit. The reason is mandatory and
    /// goes on the activity trail against the order number -- an override with
    /// nobody's name on it is how a bad debt becomes an argument.
    /// </summary>
    [HttpPost("credit-holds/{id:int}/override")]
    [Authorize(Policy = "Accountant")]
    public async Task<IActionResult> OverrideCreditHold(int id, [FromBody] OverrideRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "An override needs a reason." });

            var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            var current = await _db.OrderStatuses.Where(s => s.StatusId == order.StatusId)
                .Select(s => s.StatusKey).FirstAsync();
            if (current != "CREDIT_HOLD")
                return BadRequest(new { message = $"Order {order.OrderNo} is not on credit hold." });

            var target = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "CONFIRMED")
                         ?? await _db.OrderStatuses.FirstAsync(s => s.StatusKey == "SUBMITTED");

            var held = order.CreditHoldReason;
            order.StatusId = target.StatusId;
            order.CreditHoldReason = null;
            await _db.SaveChangesAsync();

            /* Severity 3: this is the entry an auditor comes looking for. */
            await Log("CREDIT_HOLD_OVERRIDDEN", "SalesOrder", order.OrderNo,
                $"{held} Released by override: {body.Reason.Trim()}", 3);

            var raised = (SalesInvoice?)null;
            if (body.RaiseInvoice && !await _db.SalesInvoices.AnyAsync(i => i.OrderId == id))
            {
                var lines = await _db.SalesOrderItems.AsNoTracking()
                    .Where(i => i.OrderId == id).OrderBy(i => i.LineNo)
                    .Select(i => new OrderLineRequest(
                        i.ProductId, i.Quantity, i.UnitPrice, i.DiscountPercent, i.TaxPercent))
                    .ToListAsync();

                if (lines.Count > 0)
                {
                    raised = await RaiseInvoiceForOrder(order, lines, order.MethodId, null);
                    await Log("INVOICE_CREATED", "SalesInvoice", raised.InvoiceNo,
                        $"raised on override of {order.OrderNo}", 2);
                    await TryBuildBill(raised.InvoiceId);
                }
            }

            /* ── B2 ───────────────────────────────────────────────────────
               The rep whose order was stuck is the one who has been waiting for
               this, so they are named explicitly rather than left to the role
               sweep. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept" },
                NotificationKinds.CreditHoldCleared,
                $"Limit cleared by {CurrentUserName()}",
                $"{order.OrderNo} has been released and can move again.",
                url: $"/sales/orders/{order.OrderId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null
                    ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                id,
                status = target.StatusKey,
                statusName = target.StatusName,
                invoiceId = raised?.InvoiceId,
                invoiceNo = raised?.InvoiceNo,
                message = raised is null
                    ? $"{order.OrderNo} released. It is now {target.StatusName.ToLowerInvariant()}."
                    : $"{order.OrderNo} released and invoiced as {raised.InvoiceNo}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"override the credit hold on order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  INVOICES
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The Sale Invoices ledger.
    ///
    /// Walk-in counter bills are EXCLUDED by default. They are cash off the
    /// street with no account behind them, they never age and nobody chases
    /// them, so mixing them in here buries the shop invoices that do need
    /// chasing. They are listed on their own at /sales/direct/walkin.
    /// Pass walkIn=true for only those, or walkIn=all for everything.
    /// </summary>
    [HttpGet("invoices")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? customerId,
        [FromQuery] string? walkIn,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.SalesInvoices.AsNoTracking().AsQueryable();

            if (SalesScopeUserId() is int mine)
                rows = rows.Where(i => i.CreatedByUserId == mine ||
                                       (i.Order != null && i.Order.SalesPersonUserId == mine));

            rows = (walkIn ?? "false").ToLowerInvariant() switch
            {
                "true" or "only" => rows.Where(i => i.IsWalkIn),
                "all" => rows,
                _ => rows.Where(i => !i.IsWalkIn)
            };

            /* Matches the customerId filter GetOrders already had. The party
               detail screen shows one customer's invoices, and without this it
               had to pull every invoice in the system and filter in the
               browser -- which is exactly what rule 3 of AGENTS.md forbids. */
            if (customerId is not null) rows = rows.Where(i => i.CustomerUserId == customerId);

            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(i => i.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term) ||
                                       i.CustomerUser.LegalName.ToLower().Contains(term) ||
                                       (i.WalkInName != null && i.WalkInName.ToLower().Contains(term)));
            }

            var total = await rows.CountAsync();

            var items = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(i => new
                {
                    id = i.InvoiceId,
                    invoiceNo = i.InvoiceNo,
                    orderId = i.OrderId,
                    orderNo = i.Order != null ? i.Order.OrderNo : null,
                    customerId = i.CustomerUserId,
                    customerName = i.IsWalkIn && i.WalkInName != null ? i.WalkInName : i.CustomerUser.LegalName,
                    customerPhone = i.IsWalkIn ? i.WalkInPhone : i.CustomerUser.User.Phone,
                    isWalkIn = i.IsWalkIn,
                    location = i.Location.LocationName,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    subtotal = i.Subtotal,
                    discount = i.DiscountAmount,
                    tax = i.TaxAmount,
                    total = i.TotalAmount,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    paymentMethod = i.Method.MethodKey,
                    pdfUrl = i.PdfUrl,
                    itemCount = i.SalesInvoiceItems.Count,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var shaped = items.Select(i => new
            {
                i.id, i.invoiceNo, i.orderId, i.orderNo, i.customerId, i.customerName,
                customerInitials = Initials(i.customerName),
                i.customerPhone, i.isWalkIn,
                i.location, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.total,
                i.status, i.statusName, i.paymentMethod,
                i.pdfUrl, shareUrl = ShareLink(i.invoiceNo), i.itemCount,
                i.paid, balance = i.total - i.paid
            });

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the invoice list");
        }
    }

    /// <summary>
    /// One invoice, with everything the printable document needs in a single
    /// call -- including the company letterhead off the "Company" row, so the
    /// address and tax numbers on screen are the ones in the database rather
    /// than a constant somebody typed into a component two years ago.
    /// </summary>
    [HttpGet("invoices/{id:int}")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> GetInvoice(int id)
    {
        try
        {
            if (!await MaySeeInvoice(id)) return NotYours("invoice");

            var i = await _db.SalesInvoices.AsNoTracking()
                .Where(x => x.InvoiceId == id)
                .Select(x => new
                {
                    id = x.InvoiceId,
                    invoiceNo = x.InvoiceNo,
                    orderId = x.OrderId,
                    orderNo = x.Order != null ? x.Order.OrderNo : null,
                    customerId = x.CustomerUserId,
                    accountName = x.CustomerUser.LegalName,
                    customerName = x.IsWalkIn && x.WalkInName != null ? x.WalkInName : x.CustomerUser.LegalName,
                    customerCode = x.CustomerUser.PartyCode,
                    customerPhone = x.IsWalkIn ? x.WalkInPhone : x.CustomerUser.User.Phone,
                    address = x.CustomerUser.AddressLine,
                    city = x.CustomerUser.City.CityName,
                    ntn = x.CustomerUser.Ntn,
                    strn = x.CustomerUser.Strn,
                    isWalkIn = x.IsWalkIn,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    invoiceDate = x.InvoiceDate,
                    dueDate = x.DueDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    total = x.TotalAmount,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    methodId = x.MethodId,
                    paymentMethod = x.Method.MethodKey,
                    paymentMethodName = x.Method.MethodName,
                    createdBy = x.CreatedByUser.FullName,
                    pdfUrl = x.PdfUrl,
                    notes = x.Order != null ? x.Order.Notes : null,
                    paid = x.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m,
                    lines = x.SalesInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.InvoiceItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        packing = l.Product.Packing,
                        qty = l.Quantity,
                        rate = l.UnitPrice,
                        discountPercent = l.DiscountPercent,
                        taxPercent = l.TaxPercent,
                        lineTotal = l.LineTotal,

                        /* How many of these have already come back. The return
                           form needs it: without it the screen happily lets you
                           return 50 of something that was sold 50 times and
                           returned 48 of. */
                        returnedQty = _db.SalesReturnItems
                            .Where(r => r.Return.InvoiceId == x.InvoiceId
                                     && r.ProductId == l.ProductId
                                     && r.Return.Status.StatusKey != "REJECTED")
                            .Sum(r => (int?)r.Quantity) ?? 0
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (i is null) return NotFound(new { message = $"No invoice with id {id}." });

            return Ok(new
            {
                i.id, i.invoiceNo, i.orderId, i.orderNo, i.customerId,
                i.customerName, i.accountName,
                customerInitials = Initials(i.customerName),
                i.customerCode, i.customerPhone, i.address, i.city, i.ntn, i.strn,
                i.isWalkIn, i.locationId, i.location, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.total,
                i.status, i.statusName, i.methodId, i.paymentMethod, i.paymentMethodName,
                i.createdBy, i.pdfUrl, shareUrl = ShareLink(i.invoiceNo), i.notes,
                i.paid, balance = i.total - i.paid,
                i.lines,
                company = await LetterHead()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load invoice {id}");
        }
    }

    /// <summary>
    /// Renders the bill, puts it on Cloudinary and stores the link on the row.
    ///
    /// Re-callable on purpose. Somebody changes the company address, or the
    /// first upload failed because the network dropped, and the fix has to be
    /// one button rather than a re-issued invoice number. Pass force=true to
    /// rebuild one that already has a link.
    /// </summary>
    [HttpPost("invoices/{id:int}/pdf")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> BuildInvoicePdf(int id, [FromQuery] bool force = false)
    {
        try
        {
            if (!await MaySeeInvoice(id)) return NotYours("invoice");

            var existing = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceId == id)
                .Select(i => new { i.InvoiceNo, i.PdfUrl })
                .FirstOrDefaultAsync();

            if (existing is null) return NotFound(new { message = $"No invoice with id {id}." });

            /* Even when the document is already stored, the share link is
                recomputed -- it is derived, not saved, so it costs nothing and
                it is always right for the host answering this request. */
            if (!force && !string.IsNullOrWhiteSpace(existing.PdfUrl))
                return Ok(new
                {
                    pdfUrl = existing.PdfUrl,
                    shareUrl = ShareLink(existing.InvoiceNo),
                    rebuilt = false,
                    message = "The bill was already saved."
                });

            var bill = await BuildBill(id);

            await Log("INVOICE_PDF_BUILT", "SalesInvoice", existing.InvoiceNo, bill.PdfUrl, 1);
            return Ok(new
            {
                pdfUrl = bill.PdfUrl,
                shareUrl = bill.ShareUrl,
                rebuilt = true,
                message = $"Bill for {existing.InvoiceNo} saved."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"build the bill for invoice {id}");
        }
    }

    /// <summary>
    /// Sends the caller to the bill's own file in the Cloudinary store,
    /// building and uploading it first if it has never been stored.
    ///
    /// THIS IS WHAT PRINT AND DOWNLOAD USE. Rendering fresh on every click
    /// worked, but it meant the bill people looked at was never the bill that
    /// had been stored, and the stored copy was only exercised when somebody
    /// pressed Share. Two paths to the same document is two things that can
    /// disagree. Now the bytes on screen ARE the bytes in the store -- and the
    /// same ones the customer got over WhatsApp.
    ///
    /// Falls back to rendering only when the store cannot serve it, so a
    /// Cloudinary outage degrades to "the bill still opens".
    /// </summary>
    [HttpGet("invoices/{id:int}/download")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> DownloadBill(int id, [FromQuery] bool attachment = false)
    {
        try
        {
            if (!await MaySeeInvoice(id)) return NotYours("invoice");

            var row = await _db.SalesInvoices
                .FirstOrDefaultAsync(i => i.InvoiceId == id);
            if (row is null) return NotFound(new { message = $"No invoice with id {id}." });

            if (string.IsNullOrWhiteSpace(row.PdfUrl))
                await TryBuildBill(id);

            var url = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceId == id).Select(i => i.PdfUrl).FirstAsync();

            if (!string.IsNullOrWhiteSpace(url))
                return Redirect(CloudinaryUrl.AsAttachment(url!, attachment));

            var data = await BillData(id);
            if (data is null) return NotFound(new { message = $"No invoice with id {id}." });

            Response.Headers.ContentDisposition =
                $"{(attachment ? "attachment" : "inline")}; filename=\"{data.InvoiceNo}.pdf\"";
            return File(InvoicePdf.Render(data), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"open the bill for invoice {id}");
        }
    }

    /// <summary>
    /// The bill as bytes, straight down the wire. Used by Print and by Download
    /// so neither depends on Cloudinary being reachable -- the document is
    /// rebuilt from the row every time it is asked for.
    /// </summary>
    [HttpGet("invoices/{id:int}/pdf")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> DownloadInvoicePdf(int id)
    {
        try
        {
            if (!await MaySeeInvoice(id)) return NotYours("invoice");

            var data = await BillData(id);
            if (data is null) return NotFound(new { message = $"No invoice with id {id}." });

            return File(InvoicePdf.Render(data), "application/pdf", $"{data.InvoiceNo}.pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"download the bill for invoice {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  RETURNS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("returns")]
    [Authorize(Policy = "perm:returns.sales")]
    public async Task<IActionResult> GetReturns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.SalesReturns.AsNoTracking().AsQueryable();

            /* A rep who has been given the returns right gets the returns that
               belong to their own orders -- not the whole company's. */
            if (SalesScopeUserId() is int mine)
                rows = rows.Where(r => r.CreatedByUserId == mine ||
                                       (r.Invoice.Order != null &&
                                        r.Invoice.Order.SalesPersonUserId == mine));

            if (!string.IsNullOrWhiteSpace(status))
                rows = rows.Where(r => r.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(r => r.ReturnNo.ToLower().Contains(term) ||
                                       r.CustomerUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(r => r.ReturnDate).ThenByDescending(r => r.ReturnId)
                .Select(r => new
                {
                    id = r.ReturnId,
                    returnNo = r.ReturnNo,
                    invoiceId = r.InvoiceId,
                    invoiceNo = r.Invoice.InvoiceNo,
                    customerId = r.CustomerUserId,
                    customerName = r.CustomerUser.LegalName,
                    location = r.Location.LocationName,
                    returnDate = r.ReturnDate,
                    reason = r.Reason,
                    refundMethod = r.RefundMethod.MethodKey,
                    status = r.Status.StatusKey,
                    statusName = r.Status.StatusName,
                    itemCount = r.SalesReturnItems.Count,
                    totalAmount = r.SalesReturnItems.Sum(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m,
                    resalableQty = r.SalesReturnItems
                        .Where(l => l.Condition.IsResalable).Sum(l => (int?)l.Quantity) ?? 0,
                    damagedQty = r.SalesReturnItems
                        .Where(l => !l.Condition.IsResalable).Sum(l => (int?)l.Quantity) ?? 0
                })
                .ToListAsync();

            return Ok(items.Select(r => new
            {
                r.id, r.returnNo, r.invoiceId, r.invoiceNo, r.customerId, r.customerName,
                customerInitials = Initials(r.customerName),
                r.location, r.returnDate, r.reason, r.refundMethod,
                r.status, r.statusName, r.itemCount, r.totalAmount,
                r.resalableQty, r.damagedQty
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the returns list");
        }
    }

    [HttpGet("returns/{id:int}")]
    [Authorize(Policy = "perm:returns.sales")]
    public async Task<IActionResult> GetReturn(int id)
    {
        try
        {
            if (SalesScopeUserId() is int mine &&
                !await _db.SalesReturns.AsNoTracking().AnyAsync(x =>
                    x.ReturnId == id &&
                    (x.CreatedByUserId == mine ||
                     (x.Invoice.Order != null && x.Invoice.Order.SalesPersonUserId == mine))))
                return NotYours("return");

            var r = await _db.SalesReturns.AsNoTracking()
                .Where(x => x.ReturnId == id)
                .Select(x => new
                {
                    id = x.ReturnId,
                    returnNo = x.ReturnNo,
                    invoiceId = x.InvoiceId,
                    invoiceNo = x.Invoice.InvoiceNo,
                    invoiceDate = x.Invoice.InvoiceDate,
                    invoiceTotal = x.Invoice.TotalAmount,
                    customerId = x.CustomerUserId,
                    customerName = x.CustomerUser.LegalName,
                    customerPhone = x.CustomerUser.User.Phone,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    returnDate = x.ReturnDate,
                    reason = x.Reason,
                    refundMethod = x.RefundMethod.MethodKey,
                    refundMethodName = x.RefundMethod.MethodName,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.FullName,
                    decisionReason = x.DecisionReason,
                    decidedAt = x.DecidedAt,
                    decidedBy = _db.Users.Where(u => u.UserId == x.DecidedByUserId)
                        .Select(u => u.FullName).FirstOrDefault(),
                    lines = x.SalesReturnItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.ReturnItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        qty = l.Quantity,
                        rate = l.UnitPrice,
                        condition = l.Condition.ConditionKey,
                        conditionName = l.Condition.ConditionName,
                        isResalable = l.Condition.IsResalable,
                        restockLocation = l.RestockLocation != null ? l.RestockLocation.LocationName : null,

                        /* What the original invoice sold, so the screen can show
                           "6 of 100" rather than a bare 6. */
                        soldQty = _db.SalesInvoiceItems
                            .Where(s => s.InvoiceId == x.InvoiceId && s.ProductId == l.ProductId)
                            .Sum(s => (int?)s.Quantity) ?? 0
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (r is null) return NotFound(new { message = $"No return with id {id}." });

            var activity = await _db.ActivityLogs.AsNoTracking()
                .Where(a => a.EntityReference == r.returnNo)
                .OrderBy(a => a.LoggedAt)
                .Select(a => new
                {
                    id = a.LogId,
                    action = a.ActionName,
                    detail = a.Detail,
                    at = a.LoggedAt,
                    severity = a.Severity.SeverityKey,
                    user = a.User != null ? a.User.FullName : "System"
                })
                .ToListAsync();

            return Ok(new
            {
                r.id, r.returnNo, r.invoiceId, r.invoiceNo, r.invoiceDate, r.invoiceTotal,
                r.customerId, r.customerName, r.customerPhone,
                customerInitials = Initials(r.customerName),
                r.locationId, r.location, r.returnDate, r.reason,
                r.refundMethod, r.refundMethodName,
                r.status, r.statusName, r.createdBy,
                r.decisionReason, r.decidedAt, r.decidedBy,
                totalAmount = r.lines.Sum(l => l.qty * l.rate),
                resalableQty = r.lines.Where(l => l.isResalable).Sum(l => l.qty),
                damagedQty = r.lines.Where(l => !l.isResalable).Sum(l => l.qty),
                r.lines,
                activity
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load return {id}");
        }
    }

    /// <summary>
    /// Approve, post or reject a sales return.
    ///
    /// REJECTING TAKES THE STOCK BACK OUT. CreateReturn puts resalable units
    /// straight back on the shelf so the counter can sell them again the same
    /// afternoon, which means a rejection has to undo that -- otherwise
    /// refusing a return silently gifts the warehouse free inventory, and the
    /// count only comes apart at the next stock take.
    /// </summary>
    [HttpPatch("returns/{id:int}/status")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> SetReturnStatus(int id, [FromBody] ReturnDecisionRequest body)
    {
        try
        {
            var key = (body.StatusKey ?? "").Trim().ToUpperInvariant();
            if (key is not ("APPROVED" or "POSTED" or "REJECTED" or "DRAFT"))
                return BadRequest(new { message = $"'{body.StatusKey}' is not a return decision. Use APPROVED, POSTED or REJECTED." });

            if (key == "REJECTED" && string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "Rejecting a return needs a reason." });

            var ret = await _db.SalesReturns
                .Include(r => r.SalesReturnItems).ThenInclude(l => l.Condition)
                .FirstOrDefaultAsync(r => r.ReturnId == id);
            if (ret is null) return NotFound(new { message = $"No return with id {id}." });

            var was = await _db.ReturnStatuses.Where(s => s.StatusId == ret.StatusId)
                .Select(s => new { s.StatusKey, s.StatusName }).FirstAsync();

            if (was.StatusKey == key)
                return BadRequest(new { message = $"{ret.ReturnNo} is already {was.StatusName.ToLowerInvariant()}." });
            if (was.StatusKey == "REJECTED")
                return BadRequest(new { message = $"{ret.ReturnNo} was rejected. Raise a fresh return instead of reopening this one." });
            if (was.StatusKey == "POSTED" && key != "REJECTED")
                return BadRequest(new { message = $"{ret.ReturnNo} is already posted to the ledger." });

            var target = await _db.ReturnStatuses.FirstOrDefaultAsync(s => s.StatusKey == key);
            if (target is null) return BadRequest(new { message = $"Return status '{key}' is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var pulledBack = 0;
            if (key == "REJECTED")
            {
                var saleOut = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "SALE_RETURN");

                foreach (var l in ret.SalesReturnItems.Where(l => l.Condition.IsResalable))
                {
                    var locationId = l.RestockLocationId ?? ret.LocationId;
                    var bal = await _db.StockBalances
                        .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == locationId);
                    if (bal is null) continue;

                    bal.Quantity -= l.Quantity;
                    pulledBack += l.Quantity;

                    if (saleOut is not null)
                    {
                        _db.StockMovements.Add(new StockMovement
                        {
                            ProductId = l.ProductId,
                            LocationId = locationId,
                            MovementTypeId = saleOut.MovementTypeId,
                            MovedAt = Now(),
                            ReferenceNo = ret.ReturnNo,
                            Quantity = -l.Quantity,
                            BalanceAfter = bal.Quantity,
                            UserId = CurrentUserId()
                        });
                    }

                    l.RestockLocationId = null;
                }
            }

            ret.StatusId = target.StatusId;
            ret.DecisionReason = string.IsNullOrWhiteSpace(body.Reason) ? null : body.Reason.Trim();
            ret.DecidedByUserId = CurrentUserId();
            ret.DecidedAt = Now();

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var detail = $"{was.StatusName} -> {target.StatusName}."
                       + (string.IsNullOrWhiteSpace(body.Reason) ? "" : $" {body.Reason.Trim()}")
                       + (pulledBack > 0 ? $" {pulledBack} restocked units taken back off the shelf." : "");

            await Log($"SALES_RETURN_{key}", "SalesReturn", ret.ReturnNo, detail, key == "REJECTED" ? 3 : 2);

            /* ── B7 ───────────────────────────────────────────────────────
               Warehouse is told because a return going through means stock
               physically comes back to a shelf. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "order-dept" },
                NotificationKinds.ReturnDecided,
                $"Return {target.StatusName.ToLowerInvariant()} by {CurrentUserName()}",
                key == "REJECTED"
                    ? $"{ret.ReturnNo} was refused." +
                      (string.IsNullOrWhiteSpace(body?.Reason) ? "" : $" Reason: {body!.Reason!.Trim()}")
                    : $"{ret.ReturnNo} approved -- stock is back and a credit note follows.",
                url: $"/sales/returns/{ret.ReturnId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id,
                status = target.StatusKey,
                statusName = target.StatusName,
                unitsReversed = pulledBack,
                message = key switch
                {
                    "REJECTED" => pulledBack > 0
                        ? $"{ret.ReturnNo} rejected. {pulledBack} units were taken back off the shelf."
                        : $"{ret.ReturnNo} rejected.",
                    "POSTED" => $"{ret.ReturnNo} posted.",
                    "APPROVED" => $"{ret.ReturnNo} approved.",
                    _ => $"{ret.ReturnNo} is now {target.StatusName.ToLowerInvariant()}."
                }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of return {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Everything the sales forms need to draw their pickers, in one call.
    ///
    /// `products` is here because it has to be. The order, invoice, return and
    /// counter-sale forms all used to import a hard-coded array out of the
    /// front end's src/data/products, so an item created five minutes earlier
    /// could not be sold at all -- it simply was not on the list.
    ///
    /// `defaultTaxPercent` is the rate the counter screen starts on. It used to
    /// be the literal 18 written into the markup, which meant a rate change was
    /// a code change; it now comes off the product catalogue, and the operator
    /// can still override it per line.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups([FromQuery] int? locationId)
    {
        try
        {
            var walkInId = await _db.Parties.AsNoTracking()
                .Where(p => p.PartyCode == WalkInPartyCode)
                .Select(p => (int?)p.UserId)
                .FirstOrDefaultAsync();

            var commonTax = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .GroupBy(p => p.TaxRatePercent)
                .OrderByDescending(g => g.Count())
                .Select(g => (decimal?)g.Key)
                .FirstOrDefaultAsync();

            return Ok(new
            {
                orderStatuses = await _db.OrderStatuses.AsNoTracking()
                    .OrderBy(s => s.SortOrder)
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                invoiceStatuses = await _db.InvoiceStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                returnStatuses = await _db.ReturnStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                paymentMethods = await _db.PaymentMethods.AsNoTracking()
                    .Where(m => m.IsActive)
                    .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName, kind = m.MethodKind })
                    .ToListAsync(),
                conditions = await _db.ReturnConditions.AsNoTracking()
                    .Select(c => new { id = c.ConditionId, key = c.ConditionKey, name = c.ConditionName, isResalable = c.IsResalable })
                    .ToListAsync(),
                /* Ordered so the first entry is a sane default for a till.
                   Alphabetical put "Claim Stock" -- damaged goods -- at the top
                   of the counter screen, which is the one shelf nothing should
                   ever be sold off. isSellable marks the real ones: claim and
                   in-transit stock are held, not sold. */
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive)
                    .OrderByDescending(l => l.Kind.KindKey == "shop" || l.Kind.KindKey == "warehouse")
                    .ThenBy(l => l.LocationName)
                    .Select(l => new
                    {
                        id = l.LocationId,
                        code = l.LocationCode,
                        name = l.LocationName,
                        kind = l.Kind.KindKey,
                        isSellable = l.Kind.KindKey != "claim" && l.Kind.KindKey != "transit"
                    })
                    .ToListAsync(),
                customers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive
                             && p.PartyCode != WalkInPartyCode)
                    .OrderBy(p => p.LegalName)
                    .Select(p => new
                    {
                        id = p.UserId,
                        code = p.PartyCode,
                        name = p.LegalName,
                        displayName = p.DisplayName,
                        city = p.City.CityName,
                        phone = p.User.Phone,
                        creditLimit = p.CreditLimit,
                        creditDays = p.CreditDays,
                        holdPolicy = p.HoldPolicy.PolicyKey,
                        outstanding = _db.JournalEntryLines
                            .Where(l => l.PartyUserId == p.UserId && l.Entry.StatusId == 2)
                            .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m
                    })
                    .ToListAsync(),
                salesPeople = await _db.Employees.AsNoTracking()
                    .Where(e => e.User.Role.RoleKey == "sales" && e.User.IsActive)
                    .OrderBy(e => e.User.FullName)
                    .Select(e => new { id = e.UserId, name = e.User.FullName })
                    .ToListAsync(),

                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive)
                    .OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId,
                        sku = p.Sku,
                        name = p.ProductName,
                        packing = p.Packing,
                        salePrice = p.SalePrice,
                        costPrice = p.CostPrice,
                        taxRatePercent = p.TaxRatePercent,
                        totalStock = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                        /* Stock at the till the operator is standing at, when the
                           screen says which one that is. */
                        stockHere = locationId == null
                            ? (int?)null
                            : p.StockBalances.Where(s => s.LocationId == locationId)
                                             .Sum(s => (int?)s.Quantity) ?? 0
                    })
                    .ToListAsync(),

                walkInCustomerId = walkInId,
                defaultTaxPercent = commonTax ?? 0m,
                company = await LetterHead()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load sales lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  invoice, return, counter sale
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises a sale invoice, either against an existing order or standalone.
    /// UnitCost is captured on every line at invoice time: the margin reports
    /// need what the item cost THAT DAY, and Product.CostPrice moves.
    /// </summary>
    [HttpPost("invoices")]
    [Authorize(Policy = "perm:invoices.create")]
    public async Task<IActionResult> CreateInvoice([FromBody] InvoiceRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "An invoice needs at least one line." });
            if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerId))
                return BadRequest(new { message = "Pick a valid customer." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.MethodId))
                return BadRequest(new { message = "Pick a valid payment method." });

            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
                if (l.Rate < 0) return BadRequest(new { message = "A rate cannot be negative." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            if (body.OrderId is not null)
            {
                if (!await _db.SalesOrders.AnyAsync(o => o.OrderId == body.OrderId))
                    return BadRequest(new { message = "That order does not exist." });
                if (await _db.SalesInvoices.AnyAsync(i => i.OrderId == body.OrderId))
                    return BadRequest(new { message = "That order has already been invoiced." });
            }

            var status = await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED")
                         ?? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (status is null) return BadRequest(new { message = "Invoice statuses are not configured." });

            var (subtotal, discount, tax, total) = Totals(body.Lines);

            await using var tx = await _db.Database.BeginTransactionAsync();

            var inv = new SalesInvoice
            {
                InvoiceNo = await NextNumber("INV"),
                OrderId = body.OrderId,
                CustomerUserId = body.CustomerId,
                LocationId = body.LocationId,
                InvoiceDate = body.InvoiceDate ?? Today(),
                DueDate = body.DueDate ?? (body.InvoiceDate ?? Today()).AddDays(30),
                Subtotal = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                StatusId = status.StatusId,
                MethodId = body.MethodId,
                CreatedByUserId = CurrentUserId()
            };
            _db.SalesInvoices.Add(inv);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                var cost = await _db.Products.Where(p => p.ProductId == l.ProductId)
                    .Select(p => p.CostPrice).FirstAsync();

                _db.SalesInvoiceItems.Add(new SalesInvoiceItem
                {
                    InvoiceId = inv.InvoiceId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitPrice = l.Rate,
                    DiscountPercent = l.DiscountPercent,
                    TaxPercent = l.TaxPercent,
                    UnitCost = cost,
                    LineTotal = Money(net + net * (l.TaxPercent / 100m))
                });
            }

            if (body.OrderId is not null)
            {
                var invoiced = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "INVOICED");
                var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.OrderId == body.OrderId);
                if (invoiced is not null && order is not null) order.StatusId = invoiced.StatusId;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("INVOICE_CREATED", "SalesInvoice", inv.InvoiceNo, $"{total:N0}", 2);

            var bill = await TryBuildBill(inv.InvoiceId);

            /* -- B4 -- an invoice raised with no order behind it. Worth telling
               Accounts about precisely because it did not come through the
               usual route. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.InvoiceDirect,
                $"Direct invoice by {CurrentUserName()}",
                $"{inv.InvoiceNo} -- PKR {inv.TotalAmount:N0}, raised without an order.",
                url: $"/sales/invoices/{inv.InvoiceId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = inv.InvoiceId,
                invoiceNo = inv.InvoiceNo,
                pdfUrl = bill?.PdfUrl,
                shareUrl = bill?.ShareUrl,
                total,
                message = $"Invoice {inv.InvoiceNo} raised."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "raise the invoice");
        }
    }

    /// <summary>
    /// Takes goods back from a customer. Only RESALABLE lines go back on the
    /// shelf -- damaged, expired and missing are recorded against the return so
    /// the loss is visible, but putting them back would sell a broken item twice.
    /// </summary>
    [HttpPost("returns")]
    [Authorize(Policy = "perm:returns.sales")]
    public async Task<IActionResult> CreateReturn([FromBody] ReturnRequest body)
    {
        try
        {
            /* "a salesperson can only create sales returns of his created
               order" -- so the invoice being returned against has to be one of
               theirs before anything else is checked. */
            if (!await MaySeeInvoice(body.InvoiceId)) return NotYours("invoice");

            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A return needs at least one line." });
            if (string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "A reason is required." });

            var inv = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.InvoiceId == body.InvoiceId);
            if (inv is null) return BadRequest(new { message = "Pick a valid invoice." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (!await _db.PaymentMethods.AnyAsync(m => m.MethodId == body.RefundMethodId))
                return BadRequest(new { message = "Pick a valid refund method." });

            /* Nothing may come back that never went out, and nothing may come
               back twice. Both checks are here rather than in the browser,
               because the browser is where the numbers can be edited. */
            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });

                var sold = await _db.SalesInvoiceItems
                    .Where(s => s.InvoiceId == body.InvoiceId && s.ProductId == l.ProductId)
                    .SumAsync(s => (int?)s.Quantity) ?? 0;

                if (sold == 0)
                {
                    var name = await _db.Products.Where(p => p.ProductId == l.ProductId)
                        .Select(p => p.ProductName).FirstOrDefaultAsync();
                    return BadRequest(new
                    {
                        message = $"{name ?? $"Product {l.ProductId}"} is not on invoice {inv.InvoiceNo}."
                    });
                }

                var already = await _db.SalesReturnItems
                    .Where(r => r.Return.InvoiceId == body.InvoiceId
                             && r.ProductId == l.ProductId
                             && r.Return.Status.StatusKey != "REJECTED")
                    .SumAsync(r => (int?)r.Quantity) ?? 0;

                if (already + l.Qty > sold)
                {
                    var name = await _db.Products.Where(p => p.ProductId == l.ProductId)
                        .Select(p => p.ProductName).FirstOrDefaultAsync();
                    return BadRequest(new
                    {
                        message = $"{name ?? $"Product {l.ProductId}"}: {sold} sold, {already} already returned. " +
                                  $"At most {sold - already} can come back."
                    });
                }
            }

            var status = await _db.ReturnStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (status is null) return BadRequest(new { message = "Return statuses are not configured." });

            var backIn = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "SALE_RETURN");

            await using var tx = await _db.Database.BeginTransactionAsync();

            var ret = new SalesReturn
            {
                ReturnNo = await NextNumber("SR"),
                InvoiceId = body.InvoiceId,
                CustomerUserId = inv.CustomerUserId,
                LocationId = body.LocationId,
                ReturnDate = body.ReturnDate ?? Today(),
                Reason = body.Reason.Trim(),
                RefundMethodId = body.RefundMethodId,
                StatusId = status.StatusId,
                CreatedByUserId = CurrentUserId()
            };
            _db.SalesReturns.Add(ret);
            await _db.SaveChangesAsync();

            short n = 1;
            decimal refund = 0;
            foreach (var l in body.Lines)
            {
                var cond = await _db.ReturnConditions.FirstOrDefaultAsync(c => c.ConditionId == l.ConditionId);
                if (cond is null) return BadRequest(new { message = "Pick a valid condition for every line." });

                var restockTo = cond.IsResalable ? (l.RestockLocationId ?? body.LocationId) : (int?)null;
                refund += l.Qty * l.Rate;

                _db.SalesReturnItems.Add(new SalesReturnItem
                {
                    ReturnId = ret.ReturnId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitPrice = l.Rate,
                    ConditionId = l.ConditionId,
                    RestockLocationId = restockTo
                });

                if (restockTo is null || backIn is null) continue;

                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == restockTo);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = restockTo.Value, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                    await _db.SaveChangesAsync();
                }
                bal.Quantity += l.Qty;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = restockTo.Value,
                    MovementTypeId = backIn.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = ret.ReturnNo,
                    Quantity = l.Qty,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("SALES_RETURN_CREATED", "SalesReturn", ret.ReturnNo,
                $"{body.Lines.Count} lines, {refund:N0} against {inv.InvoiceNo}. {body.Reason.Trim()}", 2);

            /* -- B6 -- the order department hears about it too: if it is
               approved, stock is coming back to a shelf they look after. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant", "order-dept" },
                NotificationKinds.ReturnRequested,
                $"Return requested by {CurrentUserName()}",
                $"{ret.ReturnNo} -- {body.Lines.Count} " +
                $"{(body.Lines.Count == 1 ? "line" : "lines")}, PKR {refund:N0} against {inv.InvoiceNo}. Needs a decision.",
                url: $"/sales/returns/{ret.ReturnId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = ret.ReturnId,
                returnNo = ret.ReturnNo,
                totalAmount = refund,
                message = $"Return {ret.ReturnNo} saved as a draft. It needs approving before the refund goes out."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the return");
        }
    }

    /// <summary>
    /// Counter sale: somebody walks in, pays, walks out. One call does the whole
    /// thing -- order, invoice, stock out, and the rendered bill -- because
    /// there is no packing or delivery step to wait for. Credit is refused
    /// here on purpose: a walk-in with no account cannot be chased.
    ///
    /// Two kinds of buyer come through this door and they are booked
    /// differently on purpose:
    ///   WALK-IN       -- no account. Booked against the shared walk-in party,
    ///                    with the person's own name and number on the invoice.
    ///                    Shows at /sales/direct/walkin.
    ///   EXISTING SHOP -- a real Party. Gets an ordinary invoice that appears
    ///                    in the Sale Invoices ledger like any other.
    /// </summary>
    [HttpPost("direct")]
    [Authorize(Policy = "OrderDept")]
    public async Task<IActionResult> CounterSale([FromBody] CounterSaleRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A counter sale needs at least one line." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

            /* Resolve who is being billed. A walk-in does not pick a customer,
               so the shared account is looked up rather than trusted from the
               browser -- otherwise anything could be posted as a walk-in. */
            int customerId;
            if (body.IsWalkIn)
            {
                var walkIn = await _db.Parties.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.PartyCode == WalkInPartyCode);
                if (walkIn is null)
                    return BadRequest(new
                    {
                        message = "The walk-in customer account is missing. Run backend/database/08_sales_documents.sql."
                    });
                customerId = walkIn.UserId;
            }
            else
            {
                if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerId && p.PartyCode != WalkInPartyCode))
                    return BadRequest(new { message = "Pick a valid shop, or switch to a walk-in sale." });
                customerId = body.CustomerId;
            }

            var method = await _db.PaymentMethods.FirstOrDefaultAsync(m => m.MethodId == body.MethodId);
            if (method is null) return BadRequest(new { message = "Pick a valid payment method." });
            if (method.MethodKey == "CREDIT" && body.IsWalkIn)
                return BadRequest(new { message = "A walk-in sale cannot be on credit -- there is no account to chase." });

            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
                if (l.Rate < 0) return BadRequest(new { message = "A rate cannot be negative." });
                if (l.TaxPercent is < 0 or > 100)
                    return BadRequest(new { message = "The tax rate must be between 0 and 100 percent." });

                var have = await _db.StockBalances
                    .Where(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId)
                    .Select(s => (int?)s.Quantity).FirstOrDefaultAsync() ?? 0;
                if (have < l.Qty)
                {
                    var p = await _db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.ProductId == l.ProductId);
                    return BadRequest(new
                    {
                        message = $"{p?.ProductName ?? $"Product {l.ProductId}"}: asked for {l.Qty}, only {have} on the shelf."
                    });
                }
            }

            var (subtotal, discount, tax, total) = Totals(body.Lines);

            var onCredit = method.MethodKey == "CREDIT";
            var delivered = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DELIVERED");
            var invoiceStatus = onCredit
                ? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED")
                : await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "PAID")
                  ?? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED");
            var saleType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "SALE");
            if (delivered is null || invoiceStatus is null || saleType is null)
                return BadRequest(new { message = "DELIVERED / PAID statuses or the SALE movement type are not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var order = new SalesOrder
            {
                OrderNo = await NextNumber("ORD"),
                CustomerUserId = customerId,
                LocationId = body.LocationId,
                SalesPersonUserId = await CurrentEmployeeId(),
                OrderDate = Today(),
                DeliveryDate = Today(),
                StatusId = delivered.StatusId,
                MethodId = body.MethodId,
                Subtotal = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                Notes = string.IsNullOrWhiteSpace(body.Notes)
                    ? (body.IsWalkIn ? "Counter sale (walk-in)" : "Counter sale")
                    : body.Notes.Trim(),
                CreatedByUserId = CurrentUserId(),
                CreatedAt = Today()
            };
            _db.SalesOrders.Add(order);
            await _db.SaveChangesAsync();

            var inv = new SalesInvoice
            {
                InvoiceNo = await NextNumber("INV"),
                OrderId = order.OrderId,
                CustomerUserId = customerId,
                LocationId = body.LocationId,
                InvoiceDate = Today(),
                DueDate = onCredit ? Today().AddDays(30) : Today(),
                Subtotal = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                StatusId = invoiceStatus.StatusId,
                MethodId = body.MethodId,
                CreatedByUserId = CurrentUserId(),
                IsWalkIn = body.IsWalkIn,
                WalkInName = body.IsWalkIn
                    ? (string.IsNullOrWhiteSpace(body.WalkInName) ? "Cash Customer" : body.WalkInName.Trim())
                    : null,
                WalkInPhone = body.IsWalkIn && !string.IsNullOrWhiteSpace(body.WalkInPhone)
                    ? body.WalkInPhone.Trim()
                    : null
            };
            _db.SalesInvoices.Add(inv);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                var lineTotal = Money(net + net * (l.TaxPercent / 100m));
                var cost = await _db.Products.Where(p => p.ProductId == l.ProductId)
                    .Select(p => p.CostPrice).FirstAsync();

                _db.SalesOrderItems.Add(new SalesOrderItem
                {
                    OrderId = order.OrderId, LineNo = n, ProductId = l.ProductId,
                    Quantity = l.Qty, UnitPrice = l.Rate,
                    DiscountPercent = l.DiscountPercent, TaxPercent = l.TaxPercent,
                    LineTotal = lineTotal
                });
                _db.SalesInvoiceItems.Add(new SalesInvoiceItem
                {
                    InvoiceId = inv.InvoiceId, LineNo = n, ProductId = l.ProductId,
                    Quantity = l.Qty, UnitPrice = l.Rate,
                    DiscountPercent = l.DiscountPercent, TaxPercent = l.TaxPercent,
                    UnitCost = cost, LineTotal = lineTotal
                });
                n++;

                var bal = await _db.StockBalances
                    .FirstAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                bal.Quantity -= l.Qty;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
                    MovementTypeId = saleType.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = inv.InvoiceNo,
                    Quantity = -l.Qty,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var who = body.IsWalkIn ? (inv.WalkInName ?? "walk-in") : "shop account";
            await Log("COUNTER_SALE", "SalesInvoice", inv.InvoiceNo,
                $"{total:N0} {method.MethodKey}, {who}", 1);

            /* The bill. Built after the transaction commits: a Cloudinary
               timeout must not roll back a sale where the cash is already in
               the drawer and the stock has walked out of the door. */
            var bill = await TryBuildBill(inv.InvoiceId);

            /* -- B5 -- deliberately NOT severe. A busy shop rings up dozens of
               these a day, and an Admin whose phone buzzes for each one turns
               notifications off within a week, taking the credit-limit alert
               with them. It lands quietly in the bell. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.CounterSale,
                $"Counter sale by {CurrentUserName()}",
                $"{inv.InvoiceNo} -- PKR {inv.TotalAmount:N0} {method.MethodName}.",
                url: $"/sales/invoices/{inv.InvoiceId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                orderId = order.OrderId,
                orderNo = order.OrderNo,
                invoiceId = inv.InvoiceId,
                invoiceNo = inv.InvoiceNo,
                isWalkIn = inv.IsWalkIn,
                customerName = inv.WalkInName ?? await _db.Parties.Where(p => p.UserId == customerId)
                    .Select(p => p.LegalName).FirstAsync(),
                customerPhone = inv.WalkInPhone ?? await _db.Users.Where(u => u.UserId == customerId)
                    .Select(u => u.Phone).FirstOrDefaultAsync(),
                subtotal, discount, tax, total,
                pdfUrl = bill?.PdfUrl,
                shareUrl = bill?.ShareUrl,
                message = onCredit
                    ? $"Sale completed. Invoice {inv.InvoiceNo}, {total:N0} on account."
                    : $"Sale completed. Invoice {inv.InvoiceNo}, {total:N0} paid by {method.MethodName}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "complete the counter sale");
        }
    }

    /// <summary>
    /// Every counter bill made out to somebody with no account, newest first.
    ///
    /// This is deliberately its own screen rather than a filter on Sale
    /// Invoices: these never age, nobody chases them, and the only thing anyone
    /// ever wants from them is the bill itself to re-send.
    /// </summary>
    [HttpGet("direct/walkin")]
    [Authorize(Policy = "OrderDept")]
    public async Task<IActionResult> GetWalkInSales(
        [FromQuery] string? q, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.SalesInvoices.AsNoTracking().Where(i => i.IsWalkIn);

            if (DateOnly.TryParse(from, out var fromDate)) rows = rows.Where(i => i.InvoiceDate >= fromDate);
            if (DateOnly.TryParse(to, out var toDate)) rows = rows.Where(i => i.InvoiceDate <= toDate);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term)
                                    || (i.WalkInName != null && i.WalkInName.ToLower().Contains(term))
                                    || (i.WalkInPhone != null && i.WalkInPhone.Contains(term)));
            }

            var total = await rows.CountAsync();
            var value = await rows.SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

            var items = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.InvoiceId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(i => new
                {
                    id = i.InvoiceId,
                    invoiceNo = i.InvoiceNo,
                    orderId = i.OrderId,
                    orderNo = i.Order != null ? i.Order.OrderNo : null,
                    customerName = i.WalkInName ?? "Cash Customer",
                    customerPhone = i.WalkInPhone,
                    invoiceDate = i.InvoiceDate,
                    location = i.Location.LocationName,
                    paymentMethod = i.Method.MethodKey,
                    paymentMethodName = i.Method.MethodName,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    itemCount = i.SalesInvoiceItems.Count,
                    units = i.SalesInvoiceItems.Sum(l => (int?)l.Quantity) ?? 0,
                    subtotal = i.Subtotal,
                    discount = i.DiscountAmount,
                    tax = i.TaxAmount,
                    total = i.TotalAmount,
                    pdfUrl = i.PdfUrl,
                    soldBy = i.CreatedByUser.FullName
                })
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                totalValue = value,
                items = items.Select(i => new
                {
                    i.id, i.invoiceNo, i.orderId, i.orderNo, i.customerName, i.customerPhone,
                    customerInitials = Initials(i.customerName),
                    i.invoiceDate, i.location, i.paymentMethod, i.paymentMethodName,
                    i.status, i.statusName, i.itemCount, i.units,
                    i.subtotal, i.discount, i.tax, i.total,
                    i.pdfUrl, shareUrl = ShareLink(i.invoiceNo), i.soldBy
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the walk-in sales");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SHARED WORKINGS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Line maths, in one place. Discount is per line and applies before tax;
    /// tax is charged on the discounted amount. Both the order path, the
    /// invoice path and the counter path call this, so the three can never
    /// disagree about what a sale came to.
    /// </summary>
    private static (decimal subtotal, decimal discount, decimal tax, decimal total)
        Totals(IEnumerable<ILine> lines)
    {
        decimal subtotal = 0, discount = 0, tax = 0;
        foreach (var l in lines)
        {
            var gross = l.Qty * l.Rate;
            var disc = Money(gross * (l.DiscountPercent / 100m));
            var net = gross - disc;
            subtotal += gross;
            discount += disc;
            tax += Money(net * (l.TaxPercent / 100m));
        }
        subtotal = Money(subtotal);
        return (subtotal, discount, tax, subtotal - discount + tax);
    }

    /// <summary>
    /// Rounds to the paisa, half away from zero -- the way a till does it.
    ///
    /// Without this a 5 percent discount on 390 came out as 1626.885, which the
    /// database rounded to 1626.89 on the way in while the JSON already sent
    /// back 1626.885, so the receipt on screen and the row in the ledger
    /// disagreed by half a paisa. Small, and exactly the kind of small that
    /// costs an afternoon at month end.
    /// </summary>
    private static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>The company letterhead, straight off the single "Company" row.</summary>
    private async Task<object?> LetterHead() =>
        await _db.Companies.AsNoTracking()
            .Select(c => new
            {
                name = c.CompanyName,
                legalName = c.LegalName,
                address = c.AddressLine,
                city = c.City.CityName,
                country = c.Country,
                phone = c.Phone,
                email = c.Email,
                ntn = c.Ntn,
                strn = c.Strn,
                currencyCode = c.CurrencyCode,
                currencySymbol = c.CurrencySymbol
            })
            .FirstOrDefaultAsync();

    /// <summary>
    /// Reads everything one bill needs and shapes it for the renderer.
    /// Returns null when the invoice does not exist.
    /// </summary>
    private async Task<InvoicePdf.Data?> BillData(int invoiceId)
    {
        var i = await _db.SalesInvoices.AsNoTracking()
            .Where(x => x.InvoiceId == invoiceId)
            .Select(x => new
            {
                x.InvoiceNo,
                x.InvoiceDate,
                x.DueDate,
                orderNo = x.Order != null ? x.Order.OrderNo : null,
                notes = x.Order != null ? x.Order.Notes : null,
                paymentMethod = x.Method.MethodKey,
                location = x.Location.LocationName,
                statusName = x.Status.StatusName,
                x.IsWalkIn,
                customerName = x.IsWalkIn && x.WalkInName != null ? x.WalkInName : x.CustomerUser.LegalName,
                customerCode = x.IsWalkIn ? null : x.CustomerUser.PartyCode,
                customerAddress = x.IsWalkIn ? null : x.CustomerUser.AddressLine,
                customerCity = x.IsWalkIn ? null : x.CustomerUser.City.CityName,
                customerPhone = x.IsWalkIn ? x.WalkInPhone : x.CustomerUser.User.Phone,
                customerNtn = x.IsWalkIn ? null : x.CustomerUser.Ntn,
                x.Subtotal,
                x.DiscountAmount,
                x.TaxAmount,
                x.TotalAmount,
                preparedBy = x.CreatedByUser.FullName,
                /* Whoever wrote the ORDER, which is not always whoever cut the
                   invoice -- a rep takes the order and the admin bills it. A
                   counter sale has no order, so it falls back to the till. */
                salesman = x.Order != null ? x.Order.CreatedByUser.FullName : x.CreatedByUser.FullName,
                paid = x.VoucherAllocations
                    .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                    .Sum(v => (decimal?)v.Amount) ?? 0m,
                lines = x.SalesInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    lineNo = (int)l.LineNo,
                    name = l.Product.ProductName,
                    sku = l.Product.Sku,
                    packing = l.Product.Packing,
                    qty = l.Quantity,
                    rate = l.UnitPrice,
                    discountPercent = l.DiscountPercent,
                    taxPercent = l.TaxPercent,
                    lineTotal = l.LineTotal
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (i is null) return null;

        var c = await _db.Companies.AsNoTracking()
            .Select(x => new
            {
                x.CompanyName, x.LegalName, x.AddressLine,
                city = x.City.CityName,
                x.Country, x.Phone, x.Email, x.Ntn, x.Strn, x.CurrencySymbol
            })
            .FirstOrDefaultAsync();

        /* A counter sale is settled the moment it is rung up, so it carries no
           voucher allocation -- the PAID status is what says it was paid for. */
        var paid = i.paid;
        if (paid == 0 && i.statusName.Equals("Paid", StringComparison.OrdinalIgnoreCase))
            paid = i.TotalAmount;

        return new InvoicePdf.Data(
            CompanyName: c?.CompanyName ?? "AdvPOS",
            CompanyLegalName: c?.LegalName ?? c?.CompanyName ?? "AdvPOS",
            CompanyAddress: c?.AddressLine ?? "",
            CompanyCity: c?.city ?? "",
            CompanyCountry: c?.Country ?? "",
            CompanyPhone: c?.Phone ?? "",
            CompanyEmail: c?.Email ?? "",
            CompanyNtn: c?.Ntn ?? "",
            CompanyStrn: c?.Strn ?? "",
            CurrencySymbol: c?.CurrencySymbol ?? "PKR",
            InvoiceNo: i.InvoiceNo,
            InvoiceDate: i.InvoiceDate,
            DueDate: i.DueDate,
            OrderNo: i.orderNo,
            PaymentMethod: i.paymentMethod,
            LocationName: i.location,
            StatusName: i.statusName,
            CustomerName: i.customerName,
            CustomerCode: i.customerCode,
            CustomerAddress: i.customerAddress,
            CustomerCity: i.customerCity,
            CustomerPhone: i.customerPhone,
            CustomerNtn: i.customerNtn,
            IsWalkIn: i.IsWalkIn,
            Subtotal: i.Subtotal,
            Discount: i.DiscountAmount,
            Tax: i.TaxAmount,
            Total: i.TotalAmount,
            Paid: paid,
            Balance: i.TotalAmount - paid,
            PreparedBy: i.preparedBy,
            Notes: i.notes,
            Lines: i.lines.Select(l => new InvoicePdf.Line(
                l.lineNo, l.name, l.sku, l.packing, l.qty, l.rate,
                l.discountPercent, l.taxPercent, l.lineTotal)).ToList(),
            Salesman: i.salesman);
    }

    /* ─────────────────────── the shareable bill link ───────────────────────

       WHY THIS EXISTS. The WhatsApp share has to hand a customer a link that
       opens on their phone, in their browser, with no account and no token.
       Cloudinary was meant to be that link, and one day it will be -- but both
       configured accounts currently refuse to DELIVER a PDF (see PdfStore),
       so a Cloudinary URL sent today would open a 401.

       So the API serves the bill itself, at a URL nobody can guess:

           /api/sales/bill/INV-26-8868?k=<hmac>

       The key is an HMAC of the invoice number under the JWT signing secret.
       Unguessable, stable for a given invoice, needs no new column, and every
       one of them is revoked at once by rotating that secret -- which is
       already on the to-do list because it is committed to a public repo.

       Cloudinary stays the preferred link and takes over automatically the
       moment PDF delivery is turned on: ShareLink only wins when the upload
       came back undeliverable. */

    private string BillKey(string invoiceNo)
    {
        var secret = _cfg["Jwt:Key"] ?? "advpos";
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = mac.ComputeHash(Encoding.UTF8.GetBytes($"bill:{invoiceNo}"));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..22];
    }

    private string ShareLink(string invoiceNo) =>
        $"{Request.Scheme}://{Request.Host}/api/sales/bill/{Uri.EscapeDataString(invoiceNo)}?k={BillKey(invoiceNo)}";

    /// <summary>
    /// The bill, to anybody holding the link. Deliberately anonymous: this is
    /// what a customer taps in WhatsApp, and they have no account here.
    ///
    /// Constant-time comparison on the key, so the endpoint cannot be used as
    /// an oracle to work one out a character at a time.
    /// </summary>
    [HttpGet("bill/{invoiceNo}")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicBill(string invoiceNo, [FromQuery] string? k)
    {
        try
        {
            var expected = BillKey(invoiceNo);
            if (string.IsNullOrEmpty(k) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(expected)))
                return NotFound(new { message = "That link is not valid." });

            var id = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceNo == invoiceNo)
                .Select(i => (int?)i.InvoiceId)
                .FirstOrDefaultAsync();
            if (id is null) return NotFound(new { message = "That bill no longer exists." });

            var data = await BillData(id.Value);
            if (data is null) return NotFound(new { message = "That bill no longer exists." });

            /* Inline, not an attachment: on a phone this should open in the
               viewer rather than land in the downloads folder. */
            Response.Headers.ContentDisposition = $"inline; filename=\"{data.InvoiceNo}.pdf\"";
            return File(InvoicePdf.Render(data), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, "open that bill");
        }
    }

    /// <summary>Renders, uploads and records the bill. Throws if any step fails.</summary>
    private async Task<Bill> BuildBill(int invoiceId)
    {
        var data = await BillData(invoiceId)
            ?? throw new InvalidOperationException($"No invoice with id {invoiceId}.");

        var bytes = InvoicePdf.Render(data);
        var stored = await PdfStore.UploadAsync(_cfg, bytes, $"{data.InvoiceNo}.pdf", "invoices");

        var row = await _db.SalesInvoices.FirstAsync(i => i.InvoiceId == invoiceId);
        row.PdfUrl = stored.Url;
        row.PdfPublicId = stored.PublicId;
        await _db.SaveChangesAsync();

        if (!stored.Deliverable)
            _logger.LogWarning(
                "Cloudinary stored {Invoice} but will not serve it ({Url}). PDF delivery is switched off on " +
                "that account -- Settings > Security > Restricted media types. Sharing the API link instead.",
                data.InvoiceNo, stored.Url);

        return new Bill(stored.Url, stored.Deliverable ? stored.Url : ShareLink(data.InvoiceNo));
    }

    /// <param name="PdfUrl">Where the document is archived (Cloudinary).</param>
    /// <param name="ShareUrl">The link to actually give a customer -- see ShareLink.</param>
    public sealed record Bill(string PdfUrl, string ShareUrl);

    /// <summary>
    /// BuildBill, but a failure is logged and swallowed.
    ///
    /// Used on the paths where a sale has already been committed. Losing the
    /// PDF is an inconvenience -- one button rebuilds it -- whereas a 500 after
    /// the stock has moved leaves the operator believing the sale did not
    /// happen and ringing it up a second time.
    /// </summary>
    private async Task<Bill?> TryBuildBill(int invoiceId)
    {
        try
        {
            return await BuildBill(invoiceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The sale was saved but its bill could not be built (invoice {InvoiceId})", invoiceId);
            return null;
        }
    }

    // ══════════════════════════ request bodies ══════════════════════════

    /// <summary>
    /// What <see cref="Totals"/> needs off a line, so the order, invoice and
    /// counter-sale bodies can all be measured by the same method without any
    /// one of them being converted into another first.
    /// </summary>
    public interface ILine
    {
        int Qty { get; }
        decimal Rate { get; }
        decimal DiscountPercent { get; }
        decimal TaxPercent { get; }
    }

    public record OrderLineRequest(
        int ProductId, int Qty, decimal Rate, decimal DiscountPercent, decimal TaxPercent) : ILine;

    public record OrderRequest(
        int CustomerId, int LocationId, int? SalesPersonUserId,
        DateOnly? OrderDate, DateOnly? DeliveryDate, DateOnly? DueDate, int MethodId,
        string? Notes, bool SaveAsDraft, bool RaiseInvoice, List<OrderLineRequest> Lines);

    public record StatusRequest(string StatusKey, string? Reason);

    public record ChangeRequestBody(string Kind, string Reason);
    public record DecideChangeBody(bool Approve, string? Note);

    public record InvoiceOrderRequest(int? MethodId, DateOnly? DueDate);

    public record OverrideRequest(string Reason, bool RaiseInvoice);

    public record InvoiceLineRequest(
        int ProductId, int Qty, decimal Rate, decimal DiscountPercent, decimal TaxPercent) : ILine;

    public record InvoiceRequest(
        int? OrderId, int CustomerId, int LocationId,
        DateOnly? InvoiceDate, DateOnly? DueDate, int MethodId,
        List<InvoiceLineRequest> Lines);

    public record ReturnLineRequest(
        int ProductId, int Qty, decimal Rate, int ConditionId, int? RestockLocationId);

    public record ReturnRequest(
        int InvoiceId, int LocationId, DateOnly? ReturnDate, string Reason,
        int RefundMethodId, List<ReturnLineRequest> Lines);

    public record ReturnDecisionRequest(string StatusKey, string? Reason);

    public record CounterSaleRequest(
        int CustomerId, bool IsWalkIn, string? WalkInName, string? WalkInPhone,
        int LocationId, int MethodId, string? Notes,
        List<InvoiceLineRequest> Lines);

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT
    // ══════════════════════════════════════════════════════════════════

    /*  The Export button used to be a toast, or nothing at all. It now returns
        a real .xlsx.

        The export runs the SAME list action the screen runs and writes its
        result, rather than re-querying -- so what lands in Excel is what was on
        the page, filters and all, and the two cannot drift.

        Money, dates and counts are written as typed cells rather than strings,
        so the columns sort and total in Excel instead of being text that merely
        looks like numbers.                                                     */

    /// <summary>Every order on the current filter, as a spreadsheet.</summary>
    [HttpGet("orders/export")]
    public async Task<IActionResult> ExportOrders(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? customerId)
    {
        try
        {
            /* pageSize 5000: an export is the one place a full list is wanted,
               and the screen itself stays paginated. */
            var action = await GetOrders(q, status, customerId, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Order No", "orderNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Customer", "customerName", XlsxWriter.CellKind.Text, 30),
                new XlsxWriter.Column("Type", "customerType"),
                new XlsxWriter.Column("City", "city"),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Sales Rep", "salesPerson", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Order Date", "orderDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Delivery Date", "deliveryDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Items", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Subtotal", "subtotal", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Discount", "discount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Tax", "tax", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Total", "total", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Payment", "paymentMethod"),
                new XlsxWriter.Column("Received", "paidAmount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Payment Status", "paymentStatus"),
                new XlsxWriter.Column("Invoice No", "invoiceNo", XlsxWriter.CellKind.Text, 16),
            };

            var bytes = XlsxWriter.FromPayload("Sales Orders",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"sales-orders-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the orders");
        }
    }

    /// <summary>Every invoice on the current filter, as a spreadsheet.</summary>
    [HttpGet("invoices/export")]
    [Authorize(Policy = "perm:invoices.view")]
    public async Task<IActionResult> ExportInvoices(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? customerId,
        [FromQuery] string? walkIn)
    {
        try
        {
            var action = await GetInvoices(q, status, customerId, walkIn, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Invoice No", "invoiceNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Order No", "orderNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Customer", "customerName", XlsxWriter.CellKind.Text, 30),
                new XlsxWriter.Column("Phone", "customerPhone", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Walk-in", "isWalkIn", XlsxWriter.CellKind.Text, 10),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Invoice Date", "invoiceDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Due Date", "dueDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Items", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Subtotal", "subtotal", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Discount", "discount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Tax", "tax", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Total", "total", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Paid", "paid", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Balance", "balance", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Payment", "paymentMethod"),
                new XlsxWriter.Column("Stored PDF", "pdfUrl", XlsxWriter.CellKind.Text, 46),
            };

            var bytes = XlsxWriter.FromPayload("Sale Invoices",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"sale-invoices-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the invoices");
        }
    }

    /// <summary>Walk-in counter bills, as a spreadsheet.</summary>
    [HttpGet("direct/walkin/export")]
    [Authorize(Policy = "OrderDept")]
    public async Task<IActionResult> ExportWalkIn(
        [FromQuery] string? q, [FromQuery] string? from, [FromQuery] string? to)
    {
        try
        {
            var action = await GetWalkInSales(q, from, to, 1, 5000);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Bill No", "invoiceNo", XlsxWriter.CellKind.Text, 16),
                new XlsxWriter.Column("Customer", "customerName", XlsxWriter.CellKind.Text, 28),
                new XlsxWriter.Column("Phone", "customerPhone", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Date", "invoiceDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Payment", "paymentMethodName"),
                new XlsxWriter.Column("Items", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Units", "units", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Subtotal", "subtotal", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Discount", "discount", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Tax", "tax", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Total", "total", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Sold By", "soldBy", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Stored PDF", "pdfUrl", XlsxWriter.CellKind.Text, 46),
            };

            var bytes = XlsxWriter.FromPayload("Walk-in Sales",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"walk-in-sales-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the walk-in sales");
        }
    }

    /// <summary>Sales returns, as a spreadsheet.</summary>
    [HttpGet("returns/export")]
    [Authorize(Policy = "perm:returns.sales")]
    public async Task<IActionResult> ExportReturns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var action = await GetReturns(q, status);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var columns = new[]
            {
                new XlsxWriter.Column("Return No", "returnNo", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Against Invoice", "invoiceNo", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Customer", "customerName", XlsxWriter.CellKind.Text, 30),
                new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 20),
                new XlsxWriter.Column("Return Date", "returnDate", XlsxWriter.CellKind.Date),
                new XlsxWriter.Column("Reason", "reason", XlsxWriter.CellKind.Text, 40),
                new XlsxWriter.Column("Refund Method", "refundMethod"),
                new XlsxWriter.Column("Status", "statusName"),
                new XlsxWriter.Column("Lines", "itemCount", XlsxWriter.CellKind.Integer, 10),
                new XlsxWriter.Column("Resalable", "resalableQty", XlsxWriter.CellKind.Integer, 12),
                new XlsxWriter.Column("Damaged", "damagedQty", XlsxWriter.CellKind.Integer, 12),
                new XlsxWriter.Column("Refund", "totalAmount", XlsxWriter.CellKind.Money),
            };

            var bytes = XlsxWriter.FromPayload("Sales Returns",
                JsonSerializer.SerializeToElement(ok.Value, ExportJson), columns);
            return File(bytes, XlsxWriter.ContentType, $"sales-returns-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "export the returns");
        }
    }

    /* The API writes anonymous objects with their own already-camelCase names;
       matching that here means the column Field values below are the same keys
       the browser sees. */
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

}
