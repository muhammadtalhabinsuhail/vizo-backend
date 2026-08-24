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
                        lineTotal = i.LineTotal
                    }).ToList(),
                    receipts = x.GoodsReceipts.Select(g => new
                    {
                        id = g.GrnId,
                        grnNo = g.GrnNo,
                        receiptDate = g.ReceiptDate,
                        status = g.Status.StatusKey
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
}
