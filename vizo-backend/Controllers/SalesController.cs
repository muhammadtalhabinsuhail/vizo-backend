using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

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
/// </summary>
[Route("api/sales")]
[ApiController]
[Authorize(Policy = "Staff")]
public class SalesController : ApiControllerBase
{
    public SalesController(AppDbContext db, IConfiguration cfg,
        ILogger<SalesController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

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

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrder(int id)
    {
        try
        {
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
                    city = x.CustomerUser.City.CityName,
                    creditLimit = x.CustomerUser.CreditLimit,
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
                    paymentMethod = x.Method.MethodKey,
                    creditHoldReason = x.CreditHoldReason,
                    notes = x.Notes,
                    createdBy = x.CreatedByUser.FullName,
                    createdAt = x.CreatedAt,
                    invoiceNo = x.SalesInvoice != null ? x.SalesInvoice.InvoiceNo : null,
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

            return Ok(new
            {
                o.id, o.orderNo, o.customerId, o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerCode, o.customerPhone, o.city, o.creditLimit,
                o.locationId, o.location, o.salesPerson,
                o.orderDate, o.deliveryDate, o.status, o.statusName,
                o.subtotal, o.discount, o.tax, o.total, o.paymentMethod,
                o.creditHoldReason, o.notes, o.createdBy, o.createdAt,
                o.invoiceNo, o.lines
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
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] OrderRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "An order needs at least one line." });
            if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerId))
                return BadRequest(new { message = "Pick a valid customer." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
                if (l.Rate < 0) return BadRequest(new { message = "A rate cannot be negative." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            decimal subtotal = 0, discount = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                subtotal += gross;
                discount += disc;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - discount + tax;

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

            await using var tx = await _db.Database.BeginTransactionAsync();

            var order = new SalesOrder
            {
                OrderNo = await NextNumber("SO"),
                CustomerUserId = body.CustomerId,
                LocationId = body.LocationId,
                SalesPersonUserId = body.SalesPersonUserId,
                OrderDate = body.OrderDate ?? Today(),
                DeliveryDate = body.DeliveryDate,
                StatusId = statusId,
                MethodId = body.MethodId,
                Subtotal = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                CreditHoldReason = overLimit
                    ? $"Order takes the balance to {wouldBe:N0} against a limit of {party!.CreditLimit:N0}."
                    : null,
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
                    LineTotal = net + net * (l.TaxPercent / 100m)
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log(overLimit ? "ORDER_CREATED_ON_HOLD" : "ORDER_CREATED",
                "SalesOrder", order.OrderNo,
                $"{body.Lines.Count} lines, {total:N0}", overLimit ? 2 : 1);

            return Ok(new
            {
                id = order.OrderId,
                orderNo = order.OrderNo,
                onCreditHold = overLimit,
                message = overLimit
                    ? $"Order {order.OrderNo} saved on credit hold -- it needs the owner's approval."
                    : $"Order {order.OrderNo} saved."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the order");
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

            order.StatusId = status.StatusId;
            await _db.SaveChangesAsync();
            await Log("ORDER_STATUS_CHANGED", "SalesOrder", order.OrderNo,
                $"-> {status.StatusName}", 1);

            return Ok(new { id, status = status.StatusKey, statusName = status.StatusName });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"change the status of order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREDIT HOLDS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The queue of orders parked over their credit limit. Accountant and owner
    /// only -- a sales rep must not see, let alone clear, this list.
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
                    creditLimit = o.CustomerUser.CreditLimit,
                    creditDays = o.CustomerUser.CreditDays,
                    holdPolicy = o.CustomerUser.HoldPolicy.PolicyKey,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    reason = o.CreditHoldReason,
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : null,
                    outstanding = _db.JournalEntryLines
                        .Where(l => l.PartyUserId == o.CustomerUserId && l.Entry.StatusId == 2)
                        .Sum(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m
                })
                .ToListAsync();

            return Ok(rows.Select(r => new
            {
                r.id, r.orderNo, r.customerId, r.customerName,
                customerInitials = Initials(r.customerName),
                r.creditLimit, r.creditDays, r.holdPolicy, r.orderDate,
                r.total, r.reason, r.salesPerson, r.outstanding
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the credit-hold queue");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  INVOICES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("invoices")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> GetInvoices(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? customerId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.SalesInvoices.AsNoTracking().AsQueryable();

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
                                       i.CustomerUser.LegalName.ToLower().Contains(term));
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
                    customerName = i.CustomerUser.LegalName,
                    location = i.Location.LocationName,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    paymentMethod = i.Method.MethodKey,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var shaped = items.Select(i => new
            {
                i.id, i.invoiceNo, i.orderId, i.orderNo, i.customerId, i.customerName,
                customerInitials = Initials(i.customerName),
                i.location, i.invoiceDate, i.dueDate, i.total, i.status, i.statusName,
                i.paymentMethod, i.paid, balance = i.total - i.paid
            });

            return Ok(new { total, page, pageSize, items = shaped });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the invoice list");
        }
    }

    [HttpGet("invoices/{id:int}")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> GetInvoice(int id)
    {
        try
        {
            var i = await _db.SalesInvoices.AsNoTracking()
                .Where(x => x.InvoiceId == id)
                .Select(x => new
                {
                    id = x.InvoiceId,
                    invoiceNo = x.InvoiceNo,
                    orderId = x.OrderId,
                    orderNo = x.Order != null ? x.Order.OrderNo : null,
                    customerId = x.CustomerUserId,
                    customerName = x.CustomerUser.LegalName,
                    customerCode = x.CustomerUser.PartyCode,
                    customerPhone = x.CustomerUser.User.Phone,
                    address = x.CustomerUser.AddressLine,
                    city = x.CustomerUser.City.CityName,
                    ntn = x.CustomerUser.Ntn,
                    location = x.Location.LocationName,
                    invoiceDate = x.InvoiceDate,
                    dueDate = x.DueDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    total = x.TotalAmount,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    paymentMethod = x.Method.MethodKey,
                    createdBy = x.CreatedByUser.FullName,
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
                        lineTotal = l.LineTotal
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (i is null) return NotFound(new { message = $"No invoice with id {id}." });

            return Ok(new
            {
                i.id, i.invoiceNo, i.orderId, i.orderNo, i.customerId, i.customerName,
                customerInitials = Initials(i.customerName),
                i.customerCode, i.customerPhone, i.address, i.city, i.ntn,
                i.location, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid, i.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load invoice {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  RETURNS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("returns")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> GetReturns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.SalesReturns.AsNoTracking().AsQueryable();

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
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> GetReturn(int id)
    {
        try
        {
            var r = await _db.SalesReturns.AsNoTracking()
                .Where(x => x.ReturnId == id)
                .Select(x => new
                {
                    id = x.ReturnId,
                    returnNo = x.ReturnNo,
                    invoiceId = x.InvoiceId,
                    invoiceNo = x.Invoice.InvoiceNo,
                    customerId = x.CustomerUserId,
                    customerName = x.CustomerUser.LegalName,
                    location = x.Location.LocationName,
                    returnDate = x.ReturnDate,
                    reason = x.Reason,
                    refundMethod = x.RefundMethod.MethodKey,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.FullName,
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
                        restockLocation = l.RestockLocation != null ? l.RestockLocation.LocationName : null
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (r is null) return NotFound(new { message = $"No return with id {id}." });

            return Ok(new
            {
                r.id, r.returnNo, r.invoiceId, r.invoiceNo, r.customerId, r.customerName,
                customerInitials = Initials(r.customerName),
                r.location, r.returnDate, r.reason, r.refundMethod,
                r.status, r.statusName, r.createdBy,
                totalAmount = r.lines.Sum(l => l.qty * l.rate),
                r.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load return {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
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
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                customers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => p.LegalName)
                    .Select(p => new
                    {
                        id = p.UserId,
                        code = p.PartyCode,
                        name = p.LegalName,
                        creditLimit = p.CreditLimit,
                        creditDays = p.CreditDays
                    })
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
            return Fail(ex, "load sales lookups");
        }
    }


    // ══════════════════════════ request bodies ══════════════════════════

    public record OrderLineRequest(
        int ProductId, int Qty, decimal Rate, decimal DiscountPercent, decimal TaxPercent);

    public record OrderRequest(
        int CustomerId, int LocationId, int? SalesPersonUserId,
        DateOnly? OrderDate, DateOnly? DeliveryDate, int MethodId,
        string? Notes, List<OrderLineRequest> Lines);

    public record StatusRequest(string StatusKey);

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  invoice, return, counter sale
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises a sale invoice, either against an existing order or standalone.
    /// UnitCost is captured on every line at invoice time: the margin reports
    /// need what the item cost THAT DAY, and Product.CostPrice moves.
    /// </summary>
    [HttpPost("invoices")]
    [Authorize(Policy = "BackOffice")]
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

            decimal subtotal = 0, discount = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                subtotal += gross;
                discount += disc;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - discount + tax;

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
                    LineTotal = net + net * (l.TaxPercent / 100m)
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
            return Ok(new { id = inv.InvoiceId, invoiceNo = inv.InvoiceNo, message = $"Invoice {inv.InvoiceNo} raised." });
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
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> CreateReturn([FromBody] ReturnRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A return needs at least one line." });
            if (string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "A reason is required." });

            var inv = await _db.SalesInvoices.FirstOrDefaultAsync(i => i.InvoiceId == body.InvoiceId);
            if (inv is null) return BadRequest(new { message = "Pick a valid invoice." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

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
            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });

                var cond = await _db.ReturnConditions.FirstOrDefaultAsync(c => c.ConditionId == l.ConditionId);
                if (cond is null) return BadRequest(new { message = "Pick a valid condition for every line." });

                _db.SalesReturnItems.Add(new SalesReturnItem
                {
                    ReturnId = ret.ReturnId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitPrice = l.Rate,
                    ConditionId = l.ConditionId,
                    RestockLocationId = cond.IsResalable ? body.LocationId : null
                });

                if (!cond.IsResalable || backIn is null) continue;

                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = body.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                    await _db.SaveChangesAsync();
                }
                bal.Quantity += l.Qty;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
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

            await Log("SALES_RETURN_CREATED", "SalesReturn", ret.ReturnNo, body.Reason, 2);
            return Ok(new { id = ret.ReturnId, returnNo = ret.ReturnNo, message = $"Return {ret.ReturnNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the return");
        }
    }

    /// <summary>
    /// Counter sale: somebody walks in, pays, walks out. One call does the whole
    /// thing -- order, invoice, stock out -- because there is no packing or
    /// delivery step to wait for. Credit is refused here on purpose: a walk-in
    /// with no account cannot be chased.
    /// </summary>
    [HttpPost("direct")]
    [Authorize(Policy = "OrderDept")]
    public async Task<IActionResult> CounterSale([FromBody] CounterSaleRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A counter sale needs at least one line." });
            if (!await _db.Parties.AnyAsync(p => p.UserId == body.CustomerId))
                return BadRequest(new { message = "Pick a valid customer (use the walk-in account for cash sales)." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

            var method = await _db.PaymentMethods.FirstOrDefaultAsync(m => m.MethodId == body.MethodId);
            if (method is null) return BadRequest(new { message = "Pick a valid payment method." });
            if (method.MethodKey == "CREDIT")
                return BadRequest(new { message = "A counter sale cannot be on credit. Take an order instead." });

            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0) return BadRequest(new { message = "Every line needs a quantity above zero." });
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

            decimal subtotal = 0, discount = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var gross = l.Qty * l.Rate;
                var disc = gross * (l.DiscountPercent / 100m);
                var net = gross - disc;
                subtotal += gross;
                discount += disc;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - discount + tax;

            var delivered = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DELIVERED");
            var paid = await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "PAID")
                       ?? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED");
            var saleType = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "SALE");
            if (delivered is null || paid is null || saleType is null)
                return BadRequest(new { message = "DELIVERED / PAID statuses or the SALE movement type are not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var order = new SalesOrder
            {
                OrderNo = await NextNumber("ORD"),
                CustomerUserId = body.CustomerId,
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
                Notes = body.Notes ?? "Counter sale",
                CreatedByUserId = CurrentUserId(),
                CreatedAt = Today()
            };
            _db.SalesOrders.Add(order);
            await _db.SaveChangesAsync();

            var inv = new SalesInvoice
            {
                InvoiceNo = await NextNumber("INV"),
                OrderId = order.OrderId,
                CustomerUserId = body.CustomerId,
                LocationId = body.LocationId,
                InvoiceDate = Today(),
                DueDate = Today(),
                Subtotal = subtotal,
                DiscountAmount = discount,
                TaxAmount = tax,
                TotalAmount = total,
                StatusId = paid.StatusId,
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
                var lineTotal = net + net * (l.TaxPercent / 100m);
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

            await Log("COUNTER_SALE", "SalesInvoice", inv.InvoiceNo, $"{total:N0} {method.MethodKey}", 1);

            return Ok(new
            {
                orderId = order.OrderId,
                orderNo = order.OrderNo,
                invoiceId = inv.InvoiceId,
                invoiceNo = inv.InvoiceNo,
                total,
                message = $"Counter sale done. Invoice {inv.InvoiceNo}, {total:N0} paid by {method.MethodName}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "complete the counter sale");
        }
    }

    // ══════════════════════ request bodies (part 2) ═════════════════════

    public record InvoiceLineRequest(
        int ProductId, int Qty, decimal Rate, decimal DiscountPercent, decimal TaxPercent);

    public record InvoiceRequest(
        int? OrderId, int CustomerId, int LocationId,
        DateOnly? InvoiceDate, DateOnly? DueDate, int MethodId,
        List<InvoiceLineRequest> Lines);

    public record ReturnLineRequest(int ProductId, int Qty, decimal Rate, int ConditionId);

    public record ReturnRequest(
        int InvoiceId, int LocationId, DateOnly? ReturnDate, string Reason,
        int RefundMethodId, List<ReturnLineRequest> Lines);

    public record CounterSaleRequest(
        int CustomerId, int LocationId, int MethodId, string? Notes,
        List<InvoiceLineRequest> Lines);
}
