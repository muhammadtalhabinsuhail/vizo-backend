using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// The /purchases screens: purchase orders, goods receipts, purchase invoices
/// and purchase returns.
///
/// The inbound chain is PO -> GRN -> PI, and the three are deliberately
/// separate things people conflate:
///   * PO  -- what we asked for.
///   * GRN -- what actually arrived. STOCK RISES HERE, not at the invoice.
///   * PI  -- the bill. The payable rises here.
/// China's own commercial invoice is theirs; the PI is our record of the
/// payable and carries SupplierInvoiceNo as the reference back to it.
///
/// TRAP: on the purchase side CreatedByUser / ReceivedByUser / ApprovedByUser
/// are all EMPLOYEE navigations, not User -- so the name is one hop further on
/// at .CreatedByUser.User.FullName. The sales side is the opposite. Getting
/// this wrong does not compile, which is the good outcome.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/purchases")]
[ApiController]
[Authorize(Policy = "BackOffice")]
public class PurchasesController : ApiControllerBase
{
    public PurchasesController(AppDbContext db, IConfiguration cfg,
        ILogger<PurchasesController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE ORDERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("orders")]
    public async Task<IActionResult> GetPurchaseOrders(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] int? supplierId)
    {
        try
        {
            var rows = _db.PurchaseOrders.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(p => p.Status.StatusKey == status);
            if (supplierId is not null) rows = rows.Where(p => p.SupplierUserId == supplierId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(p => p.PoNo.ToLower().Contains(term) ||
                                       p.SupplierUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(p => p.PoDate).ThenByDescending(p => p.PoId)
                .Select(p => new
                {
                    id = p.PoId,
                    poNo = p.PoNo,
                    supplierId = p.SupplierUserId,
                    supplierName = p.SupplierUser.LegalName,
                    location = p.Location.LocationName,
                    poDate = p.PoDate,
                    expectedDate = p.ExpectedDate,
                    status = p.Status.StatusKey,
                    statusName = p.Status.StatusName,
                    itemCount = p.PurchaseOrderItems.Count,
                    total = p.TotalAmount,
                    createdBy = p.CreatedByUser.User.FullName,
                    approvedBy = p.ApprovedByUser != null ? p.ApprovedByUser.User.FullName : null,
                    notes = p.Notes,

                    /* How much of what was ordered has actually landed. Driven by
                       the GRN lines, because the GRN is what moved stock. */
                    orderedUnits = p.PurchaseOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    receivedUnits = p.GoodsReceipts
                        .SelectMany(g => g.GoodsReceiptItems)
                        .Sum(i => (int?)i.QtyReceived) ?? 0
                })
                .ToListAsync();

            return Ok(items.Select(p => new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.location, p.poDate, p.expectedDate, p.status, p.statusName,
                p.itemCount, p.total, p.createdBy, p.approvedBy, p.notes,
                p.orderedUnits, p.receivedUnits,
                receivedPercent = p.orderedUnits == 0 ? 0
                    : (int)Math.Round(100.0 * p.receivedUnits / p.orderedUnits)
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-order list");
        }
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetPurchaseOrder(int id)
    {
        try
        {
            var p = await _db.PurchaseOrders.AsNoTracking()
                .Where(x => x.PoId == id)
                .Select(x => new
                {
                    id = x.PoId,
                    poNo = x.PoNo,
                    supplierId = x.SupplierUserId,
                    supplierName = x.SupplierUser.LegalName,
                    supplierCode = x.SupplierUser.PartyCode,
                    supplierPhone = x.SupplierUser.User.Phone,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    poDate = x.PoDate,
                    expectedDate = x.ExpectedDate,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    total = x.TotalAmount,
                    notes = x.Notes,
                    createdBy = x.CreatedByUser.User.FullName,
                    approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                    lines = x.PurchaseOrderItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.PoItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        name = i.Product.ProductName,
                        packing = i.Product.Packing,
                        qty = i.Quantity,
                        unitCost = i.UnitCost,
                        taxPercent = i.TaxPercent,
                        lineTotal = i.LineTotal,

                        /* How much of THIS line has actually arrived, counted
                           off the posted receipts for this order. The detail
                           screen shows an "x% received" figure that the list
                           endpoint already returned but this one did not, so
                           the same order read 0% when opened. Only POSTED
                           receipts count -- a draft GRN is somebody still
                           typing, not stock on the shelf. */
                        received = x.GoodsReceipts
                            .Where(g => g.Status.StatusKey == "POSTED")
                            .SelectMany(g => g.GoodsReceiptItems)
                            .Where(gi => gi.ProductId == i.ProductId)
                            .Sum(gi => (int?)(gi.QtyReceived - gi.QtyDamaged)) ?? 0
                    }).ToList(),
                    receipts = x.GoodsReceipts.OrderByDescending(g => g.ReceiptDate).Select(g => new
                    {
                        id = g.GrnId,
                        grnNo = g.GrnNo,
                        receiptDate = g.ReceiptDate,
                        status = g.Status.StatusKey,
                        statusName = g.Status.StatusName,
                        receivedBy = g.ReceivedByUser != null ? g.ReceivedByUser.User.FullName : null,
                        unitsReceived = g.GoodsReceiptItems.Sum(gi => (int?)(gi.QtyReceived - gi.QtyDamaged)) ?? 0
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (p is null) return NotFound(new { message = $"No purchase order with id {id}." });

            return Ok(new
            {
                p.id, p.poNo, p.supplierId, p.supplierName,
                supplierInitials = Initials(p.supplierName),
                p.supplierCode, p.supplierPhone, p.locationId, p.location,
                p.poDate, p.expectedDate, p.status, p.statusName,
                p.subtotal, p.discount, p.tax, p.total, p.notes,
                p.createdBy, p.approvedBy, p.lines, p.receipts
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  GOODS RECEIPTS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("grns")]
    public async Task<IActionResult> GetGrns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.GoodsReceipts.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(g => g.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(g => g.GrnNo.ToLower().Contains(term) ||
                                       g.SupplierUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(g => g.ReceiptDate).ThenByDescending(g => g.GrnId)
                .Select(g => new
                {
                    id = g.GrnId,
                    grnNo = g.GrnNo,
                    poId = g.PoId,
                    poNo = g.Po != null ? g.Po.PoNo : null,
                    supplierId = g.SupplierUserId,
                    supplierName = g.SupplierUser.LegalName,
                    location = g.Location.LocationName,
                    receiptDate = g.ReceiptDate,
                    deliveryNoteNo = g.DeliveryNoteNo,
                    vehicleNo = g.VehicleNo,
                    totalValue = g.TotalValue,
                    status = g.Status.StatusKey,
                    statusName = g.Status.StatusName,
                    receivedBy = g.ReceivedByUser.User.FullName,
                    itemCount = g.GoodsReceiptItems.Count,
                    unitsReceived = g.GoodsReceiptItems.Sum(i => (int?)i.QtyReceived) ?? 0,
                    unitsDamaged = g.GoodsReceiptItems.Sum(i => (int?)i.QtyDamaged) ?? 0
                })
                .ToListAsync();

            return Ok(items.Select(g => new
            {
                g.id, g.grnNo, g.poId, g.poNo, g.supplierId, g.supplierName,
                supplierInitials = Initials(g.supplierName),
                g.location, g.receiptDate, g.deliveryNoteNo, g.vehicleNo,
                g.totalValue, g.status, g.statusName, g.receivedBy,
                g.itemCount, g.unitsReceived, g.unitsDamaged,
                unitsAccepted = g.unitsReceived - g.unitsDamaged
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the goods-receipt list");
        }
    }

    [HttpGet("grns/{id:int}")]
    public async Task<IActionResult> GetGrn(int id)
    {
        try
        {
            var g = await _db.GoodsReceipts.AsNoTracking()
                .Where(x => x.GrnId == id)
                .Select(x => new
                {
                    id = x.GrnId,
                    grnNo = x.GrnNo,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    supplierId = x.SupplierUserId,
                    supplierName = x.SupplierUser.LegalName,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    receiptDate = x.ReceiptDate,
                    deliveryNoteNo = x.DeliveryNoteNo,
                    vehicleNo = x.VehicleNo,
                    totalValue = x.TotalValue,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    receivedBy = x.ReceivedByUser.User.FullName,
                    notes = x.Notes,
                    lines = x.GoodsReceiptItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        id = i.GrnItemId,
                        lineNo = i.LineNo,
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        name = i.Product.ProductName,
                        qtyReceived = i.QtyReceived,
                        qtyDamaged = i.QtyDamaged,
                        qtyAccepted = i.QtyReceived - i.QtyDamaged,
                        unitCost = i.UnitCost,
                        batchNo = i.BatchNo,
                        expiryDate = i.ExpiryDate
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (g is null) return NotFound(new { message = $"No goods receipt with id {id}." });

            return Ok(new
            {
                g.id, g.grnNo, g.poId, g.poNo, g.supplierId, g.supplierName,
                supplierInitials = Initials(g.supplierName),
                g.locationId, g.location, g.receiptDate, g.deliveryNoteNo, g.vehicleNo,
                g.totalValue, g.status, g.statusName, g.receivedBy, g.notes, g.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load goods receipt {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE INVOICES
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("invoices")]
    public async Task<IActionResult> GetPurchaseInvoices([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.PurchaseInvoices.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(i => i.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(i => i.InvoiceNo.ToLower().Contains(term) ||
                                       i.SupplierInvoiceNo.ToLower().Contains(term) ||
                                       i.SupplierUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.PiId)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = i.SupplierUser.LegalName,
                    poId = i.PoId,
                    poNo = i.Po != null ? i.Po.PoNo : null,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    subtotal = i.Subtotal,
                    discount = i.DiscountAmount,
                    tax = i.TaxAmount,
                    whtAmount = i.WhtAmount,
                    total = i.TotalAmount,
                    status = i.Status.StatusKey,
                    statusName = i.Status.StatusName,
                    paymentMethod = i.Method.MethodKey,
                    createdBy = i.CreatedByUser.User.FullName,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            return Ok(items.Select(i => new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid,
                isOverdue = i.total - i.paid > 0 && i.dueDate < Today()
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-invoice list");
        }
    }

    [HttpGet("invoices/{id:int}")]
    public async Task<IActionResult> GetPurchaseInvoice(int id)
    {
        try
        {
            var i = await _db.PurchaseInvoices.AsNoTracking()
                .Where(x => x.PiId == id)
                .Select(x => new
                {
                    id = x.PiId,
                    invoiceNo = x.InvoiceNo,
                    supplierInvoiceNo = x.SupplierInvoiceNo,
                    supplierId = x.SupplierUserId,
                    supplierName = x.SupplierUser.LegalName,
                    supplierCode = x.SupplierUser.PartyCode,
                    poId = x.PoId,
                    poNo = x.Po != null ? x.Po.PoNo : null,
                    invoiceDate = x.InvoiceDate,
                    dueDate = x.DueDate,
                    subtotal = x.Subtotal,
                    discount = x.DiscountAmount,
                    tax = x.TaxAmount,
                    whtAmount = x.WhtAmount,
                    total = x.TotalAmount,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    paymentMethod = x.Method.MethodKey,
                    createdBy = x.CreatedByUser.User.FullName,
                    paid = x.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m,
                    lines = x.PurchaseInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.PiItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        sku = l.Product.Sku,
                        name = l.Product.ProductName,
                        qty = l.Quantity,
                        unitCost = l.UnitCost,
                        taxPercent = l.TaxPercent,
                        lineTotal = l.LineTotal
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (i is null) return NotFound(new { message = $"No purchase invoice with id {id}." });

            return Ok(new
            {
                i.id, i.invoiceNo, i.supplierInvoiceNo, i.supplierId, i.supplierName,
                supplierInitials = Initials(i.supplierName),
                i.supplierCode, i.poId, i.poNo, i.invoiceDate, i.dueDate,
                i.subtotal, i.discount, i.tax, i.whtAmount, i.total,
                i.status, i.statusName, i.paymentMethod, i.createdBy,
                i.paid, balance = i.total - i.paid, i.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase invoice {id}");
        }
    }

    /// <summary>
    /// Supplier payables due inside `withinDays`, oldest first. Feeds the
    /// accountant's payables screen and the payment reminders.
    /// </summary>
    [HttpGet("payables")]
    public async Task<IActionResult> GetPayables([FromQuery] int withinDays = 30)
    {
        try
        {
            var cutoff = Today().AddDays(withinDays);

            var rows = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.DueDate <= cutoff)
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplierInvoiceNo = i.SupplierInvoiceNo,
                    supplierId = i.SupplierUserId,
                    supplierName = i.SupplierUser.LegalName,
                    invoiceDate = i.InvoiceDate,
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var open = rows.Where(r => r.total - r.paid > 0)
                .OrderBy(r => r.dueDate)
                .Select(r => new
                {
                    r.id, r.invoiceNo, r.supplierInvoiceNo, r.supplierId, r.supplierName,
                    supplierInitials = Initials(r.supplierName),
                    r.invoiceDate, r.dueDate, r.total, r.paid,
                    balance = r.total - r.paid,
                    daysToDue = r.dueDate.DayNumber - Today().DayNumber,
                    isOverdue = r.dueDate < Today()
                })
                .ToList();

            return Ok(new
            {
                withinDays,
                count = open.Count,
                totalDue = open.Sum(o => o.balance),
                overdueCount = open.Count(o => o.isOverdue),
                overdueTotal = open.Where(o => o.isOverdue).Sum(o => o.balance),
                items = open
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load supplier payables");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE RETURNS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("returns")]
    public async Task<IActionResult> GetPurchaseReturns([FromQuery] string? q, [FromQuery] string? status)
    {
        try
        {
            var rows = _db.PurchaseReturns.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(r => r.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(r => r.ReturnNo.ToLower().Contains(term) ||
                                       r.SupplierUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(r => r.ReturnDate).ThenByDescending(r => r.PrId)
                .Select(r => new
                {
                    id = r.PrId,
                    returnNo = r.ReturnNo,
                    piId = r.PiId,
                    invoiceNo = r.Pi.InvoiceNo,
                    supplierId = r.SupplierUserId,
                    supplierName = r.SupplierUser.LegalName,
                    location = r.Location.LocationName,
                    returnDate = r.ReturnDate,
                    reason = r.Reason,
                    status = r.Status.StatusKey,
                    statusName = r.Status.StatusName,
                    createdBy = r.CreatedByUser.User.FullName,
                    itemCount = r.PurchaseReturnItems.Count,
                    totalAmount = r.PurchaseReturnItems.Sum(l => (decimal?)(l.Quantity * l.UnitCost)) ?? 0m
                })
                .ToListAsync();

            return Ok(items.Select(r => new
            {
                r.id, r.returnNo, r.piId, r.invoiceNo, r.supplierId, r.supplierName,
                supplierInitials = Initials(r.supplierName),
                r.location, r.returnDate, r.reason, r.status, r.statusName,
                r.createdBy, r.itemCount, r.totalAmount
            }));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the purchase-returns list");
        }
    }

    [HttpGet("returns/{id:int}")]
    public async Task<IActionResult> GetPurchaseReturn(int id)
    {
        try
        {
            var r = await _db.PurchaseReturns.AsNoTracking()
                .Where(x => x.PrId == id)
                .Select(x => new
                {
                    id = x.PrId,
                    returnNo = x.ReturnNo,
                    piId = x.PiId,
                    invoiceNo = x.Pi.InvoiceNo,
                    supplierId = x.SupplierUserId,
                    supplierName = x.SupplierUser.LegalName,
                    location = x.Location.LocationName,
                    returnDate = x.ReturnDate,
                    reason = x.Reason,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    createdBy = x.CreatedByUser.User.FullName,
                    lines = x.PurchaseReturnItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.PrItemId,
                        lineNo = l.LineNo,
                        productId = l.ProductId,
                        sku = l.Product.Sku,
                        name = l.Product.ProductName,
                        qty = l.Quantity,
                        unitCost = l.UnitCost,
                        lineTotal = l.Quantity * l.UnitCost
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (r is null) return NotFound(new { message = $"No purchase return with id {id}." });

            return Ok(new
            {
                r.id, r.returnNo, r.piId, r.invoiceNo, r.supplierId, r.supplierName,
                supplierInitials = Initials(r.supplierName),
                r.location, r.returnDate, r.reason, r.status, r.statusName, r.createdBy,
                totalAmount = r.lines.Sum(l => l.lineTotal),
                r.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load purchase return {id}");
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
                suppliers = await _db.Parties.AsNoTracking()
                    .Where(p => (p.User.RoleId == 6 || p.User.RoleId == 7) && p.User.IsActive)
                    .OrderBy(p => p.LegalName)
                    .Select(p => new { id = p.UserId, code = p.PartyCode, name = p.LegalName })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                poStatuses = await _db.PurchaseOrderStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                postingStatuses = await _db.PostingStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                invoiceStatuses = await _db.InvoiceStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName })
                    .ToListAsync(),
                paymentMethods = await _db.PaymentMethods.AsNoTracking()
                    .Where(m => m.IsActive)
                    .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName })
                    .ToListAsync(),
                products = await _db.Products.AsNoTracking()
                    .Where(p => p.IsActive).OrderBy(p => p.ProductName)
                    .Select(p => new
                    {
                        id = p.ProductId, sku = p.Sku, name = p.ProductName,
                        costPrice = p.CostPrice, packing = p.Packing,
                        taxRatePercent = p.TaxRatePercent
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load purchase lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CREATE  --  PO, GRN, PI, PR
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises a purchase order. Line totals are recomputed here from qty, cost
    /// and tax; a total that arrives from the browser is a total anybody can
    /// edit.
    /// </summary>
    [HttpPost("orders")]
    public async Task<IActionResult> CreatePurchaseOrder([FromBody] PoRequest body)
    {
        try
        {
            var err = await ValidateLines(body.Lines, body.SupplierId, body.LocationId);
            if (err is not null) return BadRequest(new { message = err });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can raise a purchase order." });

            var status = await _db.PurchaseOrderStatuses
                .FirstOrDefaultAsync(s => s.StatusKey == (body.SubmitForApproval ? "PENDING_APPROVAL" : "DRAFT"));
            if (status is null) return BadRequest(new { message = "Purchase-order statuses are not configured." });

            decimal subtotal = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                subtotal += net;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - body.Discount + tax;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var po = new PurchaseOrder
            {
                PoNo = await NextNumber("PO"),
                SupplierUserId = body.SupplierId,
                LocationId = body.LocationId,
                PoDate = body.PoDate ?? Today(),
                ExpectedDate = body.ExpectedDate,
                StatusId = status.StatusId,
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = tax,
                TotalAmount = total,
                Notes = body.Notes,
                CreatedByUserId = me.Value,
                ApprovedByUserId = null
            };
            _db.PurchaseOrders.Add(po);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                _db.PurchaseOrderItems.Add(new PurchaseOrderItem
                {
                    PoId = po.PoId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost,
                    TaxPercent = l.TaxPercent,
                    LineTotal = net + net * (l.TaxPercent / 100m)
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PO_CREATED", "PurchaseOrder", po.PoNo, $"{body.Lines.Count} lines, {total:N0}", 1);
            return Ok(new { id = po.PoId, poNo = po.PoNo, message = $"Purchase order {po.PoNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase order");
        }
    }

    /// <summary>
    /// Approves a purchase order. Separate from creation on purpose: whoever
    /// raises the order is not necessarily allowed to commit the money.
    /// </summary>
    [HttpPost("orders/{id:int}/approve")]
    public async Task<IActionResult> ApprovePurchaseOrder(int id)
    {
        try
        {
            var po = await _db.PurchaseOrders.Include(p => p.Status)
                .FirstOrDefaultAsync(p => p.PoId == id);
            if (po is null) return NotFound(new { message = $"No purchase order with id {id}." });
            if (po.Status.StatusKey is "APPROVED" or "RECEIVED" or "CLOSED")
                return BadRequest(new { message = $"{po.PoNo} is already {po.Status.StatusName}." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can approve a purchase order." });

            var approved = await _db.PurchaseOrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "APPROVED");
            if (approved is null) return BadRequest(new { message = "No APPROVED status is configured." });

            po.StatusId = approved.StatusId;
            po.ApprovedByUserId = me.Value;
            await _db.SaveChangesAsync();
            await Log("PO_APPROVED", "PurchaseOrder", po.PoNo, $"{po.TotalAmount:N0}", 2);

            return Ok(new { id, message = $"{po.PoNo} approved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"approve purchase order {id}");
        }
    }

    /// <summary>
    /// Records a goods receipt. THIS IS WHERE STOCK RISES -- not at the invoice.
    /// Damaged units are received but not added to sellable stock.
    /// </summary>
    [HttpPost("grns")]
    public async Task<IActionResult> CreateGrn([FromBody] GrnRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A goods receipt needs at least one line." });
            if (!await _db.Parties.AnyAsync(p => p.UserId == body.SupplierId))
                return BadRequest(new { message = "Pick a valid supplier." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });
            if (string.IsNullOrWhiteSpace(body.DeliveryNoteNo))
                return BadRequest(new { message = "The supplier's delivery-note number is required." });

            foreach (var l in body.Lines)
            {
                if (l.QtyReceived <= 0)
                    return BadRequest(new { message = "Every line needs a received quantity above zero." });
                if (l.QtyDamaged < 0 || l.QtyDamaged > l.QtyReceived)
                    return BadRequest(new { message = "Damaged cannot be negative or more than received." });
                if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                    return BadRequest(new { message = $"Product {l.ProductId} does not exist." });
            }

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can receive stock." });

            var posted = await _db.PostingStatuses.FirstOrDefaultAsync(s => s.StatusKey == "POSTED");
            if (posted is null) return BadRequest(new { message = "No POSTED status is configured." });

            var receipt = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "PURCHASE")
                          ?? await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "RECEIPT");
            if (receipt is null) return BadRequest(new { message = "No inbound movement type is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var grn = new GoodsReceipt
            {
                GrnNo = await NextNumber("GRN"),
                PoId = body.PoId,
                SupplierUserId = body.SupplierId,
                LocationId = body.LocationId,
                ReceiptDate = body.ReceiptDate ?? Today(),
                DeliveryNoteNo = body.DeliveryNoteNo.Trim(),
                VehicleNo = body.VehicleNo,
                TotalValue = body.Lines.Sum(l => l.QtyReceived * l.UnitCost),
                StatusId = posted.StatusId,
                ReceivedByUserId = me.Value,
                Notes = body.Notes
            };
            _db.GoodsReceipts.Add(grn);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                _db.GoodsReceiptItems.Add(new GoodsReceiptItem
                {
                    GrnId = grn.GrnId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    QtyReceived = l.QtyReceived,
                    QtyDamaged = l.QtyDamaged,
                    UnitCost = l.UnitCost,
                    BatchNo = l.BatchNo,
                    ExpiryDate = l.ExpiryDate
                });

                /* Only ACCEPTED units go on the shelf. Damaged ones are recorded
                   on the line so the claim against the supplier has evidence,
                   but they are not sellable and must not inflate stock. */
                var accepted = l.QtyReceived - l.QtyDamaged;
                if (accepted <= 0) continue;

                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = l.ProductId, LocationId = body.LocationId, Quantity = 0 };
                    _db.StockBalances.Add(bal);
                }
                bal.Quantity += accepted;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = l.ProductId,
                    LocationId = body.LocationId,
                    MovementTypeId = receipt.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = grn.GrnNo,
                    Quantity = accepted,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }
            await _db.SaveChangesAsync();

            /* Move the PO along so the buyer can see what has landed. */
            if (body.PoId is not null)
            {
                var po = await _db.PurchaseOrders
                    .Include(p => p.PurchaseOrderItems)
                    .Include(p => p.GoodsReceipts).ThenInclude(g => g.GoodsReceiptItems)
                    .FirstOrDefaultAsync(p => p.PoId == body.PoId);
                if (po is not null)
                {
                    var ordered = po.PurchaseOrderItems.Sum(i => i.Quantity);
                    var got = po.GoodsReceipts.SelectMany(g => g.GoodsReceiptItems).Sum(i => i.QtyReceived);
                    var key = got >= ordered ? "RECEIVED" : "PARTIALLY_RECEIVED";
                    var st = await _db.PurchaseOrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == key);
                    if (st is not null) po.StatusId = st.StatusId;
                    await _db.SaveChangesAsync();
                }
            }

            await tx.CommitAsync();
            await Log("GRN_CREATED", "GoodsReceipt", grn.GrnNo,
                $"{body.Lines.Count} lines, {grn.TotalValue:N0}", 1);

            return Ok(new { id = grn.GrnId, grnNo = grn.GrnNo, message = $"{grn.GrnNo} received and stock updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "record the goods receipt");
        }
    }

    /// <summary>
    /// Records the supplier's bill. The PAYABLE rises here; stock does not move
    /// (that happened at the GRN). SupplierInvoiceNo is their number on their
    /// paper -- ours is generated.
    /// </summary>
    [HttpPost("invoices")]
    public async Task<IActionResult> CreatePurchaseInvoice([FromBody] PiRequest body)
    {
        try
        {
            var err = await ValidateLines(body.Lines, body.SupplierId, null);
            if (err is not null) return BadRequest(new { message = err });
            if (string.IsNullOrWhiteSpace(body.SupplierInvoiceNo))
                return BadRequest(new { message = "The supplier's own invoice number is required." });
            if (body.WhtAmount < 0)
                return BadRequest(new { message = "Withholding tax cannot be negative." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can record a purchase invoice." });

            var status = await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "ISSUED")
                         ?? await _db.InvoiceStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (status is null) return BadRequest(new { message = "Invoice statuses are not configured." });

            decimal subtotal = 0, tax = 0;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                subtotal += net;
                tax += net * (l.TaxPercent / 100m);
            }
            var total = subtotal - body.Discount + tax - body.WhtAmount;

            await using var tx = await _db.Database.BeginTransactionAsync();

            var pi = new PurchaseInvoice
            {
                InvoiceNo = await NextNumber("PI"),
                SupplierInvoiceNo = body.SupplierInvoiceNo.Trim(),
                SupplierUserId = body.SupplierId,
                PoId = body.PoId,
                InvoiceDate = body.InvoiceDate ?? Today(),
                DueDate = body.DueDate ?? (body.InvoiceDate ?? Today()).AddDays(30),
                Subtotal = subtotal,
                DiscountAmount = body.Discount,
                TaxAmount = tax,
                WhtAmount = body.WhtAmount,
                TotalAmount = total,
                StatusId = status.StatusId,
                MethodId = body.MethodId,
                CreatedByUserId = me.Value
            };
            _db.PurchaseInvoices.Add(pi);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                var net = l.Qty * l.UnitCost;
                _db.PurchaseInvoiceItems.Add(new PurchaseInvoiceItem
                {
                    PiId = pi.PiId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost,
                    TaxPercent = l.TaxPercent,
                    LineTotal = net + net * (l.TaxPercent / 100m)
                });
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PI_CREATED", "PurchaseInvoice", pi.InvoiceNo,
                $"{body.SupplierInvoiceNo} / {total:N0}", 2);

            return Ok(new { id = pi.PiId, invoiceNo = pi.InvoiceNo, message = $"Purchase invoice {pi.InvoiceNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase invoice");
        }
    }

    /// <summary>Sends goods back to a supplier and takes them off the shelf.</summary>
    [HttpPost("returns")]
    public async Task<IActionResult> CreatePurchaseReturn([FromBody] PrRequest body)
    {
        try
        {
            if (body.Lines is null || body.Lines.Count == 0)
                return BadRequest(new { message = "A purchase return needs at least one line." });
            if (string.IsNullOrWhiteSpace(body.Reason))
                return BadRequest(new { message = "A reason is required." });

            var pi = await _db.PurchaseInvoices.FirstOrDefaultAsync(p => p.PiId == body.PiId);
            if (pi is null) return BadRequest(new { message = "Pick a valid purchase invoice." });
            if (!await _db.Locations.AnyAsync(l => l.LocationId == body.LocationId))
                return BadRequest(new { message = "Pick a valid location." });

            var me = await CurrentEmployeeId();
            if (me is null) return BadRequest(new { message = "Only a staff account can raise a purchase return." });

            var status = await _db.ReturnStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DRAFT");
            if (status is null) return BadRequest(new { message = "Return statuses are not configured." });

            var issue = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "PURCHASE_RETURN")
                        ?? await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "ISSUE");

            await using var tx = await _db.Database.BeginTransactionAsync();

            var pr = new PurchaseReturn
            {
                ReturnNo = await NextNumber("PR"),
                PiId = body.PiId,
                SupplierUserId = pi.SupplierUserId,
                LocationId = body.LocationId,
                ReturnDate = body.ReturnDate ?? Today(),
                Reason = body.Reason.Trim(),
                StatusId = status.StatusId,
                CreatedByUserId = me.Value
            };
            _db.PurchaseReturns.Add(pr);
            await _db.SaveChangesAsync();

            short n = 1;
            foreach (var l in body.Lines)
            {
                if (l.Qty <= 0)
                    return BadRequest(new { message = "Every line needs a quantity above zero." });

                _db.PurchaseReturnItems.Add(new PurchaseReturnItem
                {
                    PrId = pr.PrId,
                    LineNo = n++,
                    ProductId = l.ProductId,
                    Quantity = l.Qty,
                    UnitCost = l.UnitCost
                });

                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == l.ProductId && s.LocationId == body.LocationId);
                if (bal is null || bal.Quantity < l.Qty)
                    return BadRequest(new
                    {
                        message = $"Cannot return {l.Qty} of product {l.ProductId} -- only {bal?.Quantity ?? 0} on hand."
                    });

                bal.Quantity -= l.Qty;
                if (issue is not null)
                {
                    _db.StockMovements.Add(new StockMovement
                    {
                        ProductId = l.ProductId,
                        LocationId = body.LocationId,
                        MovementTypeId = issue.MovementTypeId,
                        MovedAt = Now(),
                        ReferenceNo = pr.ReturnNo,
                        Quantity = -l.Qty,
                        BalanceAfter = bal.Quantity,
                        UserId = CurrentUserId()
                    });
                }
            }
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("PR_CREATED", "PurchaseReturn", pr.ReturnNo, body.Reason, 2);
            return Ok(new { id = pr.PrId, returnNo = pr.ReturnNo, message = $"Purchase return {pr.ReturnNo} saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save the purchase return");
        }
    }

    // ════════════════════════ validation helper ════════════════════════

    private async Task<string?> ValidateLines(List<PurchaseLineRequest>? lines, int supplierId, int? locationId)
    {
        if (lines is null || lines.Count == 0) return "At least one line is required.";
        if (!await _db.Parties.AnyAsync(p => p.UserId == supplierId)) return "Pick a valid supplier.";
        if (locationId is not null && !await _db.Locations.AnyAsync(l => l.LocationId == locationId))
            return "Pick a valid location.";

        foreach (var l in lines)
        {
            if (l.Qty <= 0) return "Every line needs a quantity above zero.";
            if (l.UnitCost < 0) return "A unit cost cannot be negative.";
            if (l.TaxPercent is < 0 or > 100) return "Tax must be between 0 and 100.";
            if (!await _db.Products.AnyAsync(p => p.ProductId == l.ProductId))
                return $"Product {l.ProductId} does not exist.";
        }
        return null;
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record PurchaseLineRequest(int ProductId, int Qty, decimal UnitCost, decimal TaxPercent);

    public record PoRequest(
        int SupplierId, int LocationId, DateOnly? PoDate, DateOnly? ExpectedDate,
        decimal Discount, string? Notes, bool SubmitForApproval,
        List<PurchaseLineRequest> Lines);

    public record GrnLineRequest(
        int ProductId, int QtyReceived, int QtyDamaged, decimal UnitCost,
        string? BatchNo, DateOnly? ExpiryDate);

    public record GrnRequest(
        int? PoId, int SupplierId, int LocationId, DateOnly? ReceiptDate,
        string DeliveryNoteNo, string? VehicleNo, string? Notes,
        List<GrnLineRequest> Lines);

    public record PiRequest(
        int SupplierId, int? PoId, string SupplierInvoiceNo,
        DateOnly? InvoiceDate, DateOnly? DueDate,
        decimal Discount, decimal WhtAmount, int MethodId,
        List<PurchaseLineRequest> Lines);

    public record PrRequest(
        int PiId, int LocationId, DateOnly? ReturnDate, string Reason,
        List<PurchaseLineRequest> Lines);
}
