using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Everything that ever happened to ONE product, in the three shapes people
/// ask for it:
///
///   GET  movements                 each stock movement as a card -- a transfer
///                                  is one card with a from and a to, not two
///                                  half-rows that have to be read together
///   GET  movements/{movementId}    one movement in full: who, the document it
///                                  belongs to, both legs of a transfer, and the
///                                  rest of that document's lines
///   GET  history                   the product's whole life as a timeline --
///                                  ordered from the supplier, received, sold,
///                                  packed, dispatched, delivered, returned,
///                                  moved, corrected -- with the figures a
///                                  person asks first
///   GET  ledger                    the stock card: opening balance, every unit
///                                  in and out, the running total
///   GET  history/export            all of the above as one workbook
///
/// ─────────────────────────── WHERE IT COMES FROM ───────────────────────────
///
/// Nothing new is stored. Every line here is read from the documents that
/// already exist -- purchase orders, goods receipts, supplier bills, customer
/// orders, invoices, deliveries, returns, transfers, stock corrections, claims
/// -- plus the activity log for the order's own journey through the chain. A
/// history kept in a table of its own would be a second record of the same
/// facts, and the day the two disagree nobody could say which is right.
///
/// Each query is filtered by product first, so the size of the work is the
/// size of THIS product's life, not of the database. Paging is done on the
/// merged list, because the order of events only exists once they are merged.
/// </summary>
[Route("api/inventory/products/{productId:int}")]
[ApiController]
[Authorize(Policy = "BackOffice")]
public class ProductHistoryController : ApiControllerBase
{
    public ProductHistoryController(AppDbContext db, IConfiguration cfg,
        ILogger<ProductHistoryController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    // ══════════════════════════════════════════════════════════════════
    //  MOVEMENTS AS CARDS
    // ══════════════════════════════════════════════════════════════════

    private const string TransferOut = "TRANSFER_OUT";
    private const string TransferIn = "TRANSFER_IN";

    /// <summary>
    /// The product's stock movements, newest first, one card each -- except a
    /// transfer, which is ONE card however many legs it has.
    ///
    /// A transfer writes two rows: out of the sending shelf, into the receiving
    /// one, often hours apart. Shown as rows, a person has to find the pair and
    /// read them together to learn the one thing they wanted, which is "from
    /// where to where, and how many". Shown as a card, that is the card.
    /// </summary>
    [HttpGet("movements")]
    public async Task<IActionResult> Movements(int productId, [FromQuery] string? kind,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 24)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 100) pageSize = 24;

            if (!await _db.Products.AnyAsync(p => p.ProductId == productId))
                return NotFound(new { message = $"No product with id {productId}." });

            var cards = await MovementCards(productId);

            var counts = cards.GroupBy(c => c.Kind)
                .Select(g => new { kind = g.Key, count = g.Count() })
                .ToList();

            if (!string.IsNullOrWhiteSpace(kind))
                cards = cards.Where(c => c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = cards.Count;
            var items = cards.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new { total, page, pageSize, counts, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the movements of product {productId}");
        }
    }

    /// <summary>One card on the Movements tab.</summary>
    public sealed record MovementCard(
        string Key, int MovementId, string Kind, string Type, string TypeName,
        DateTime At, string? Reference, int? DocumentId, string? DocumentUrl,
        string Direction, int Qty,
        string? Location, string? FromLocation, string? ToLocation,
        int? BalanceAfter, string? Status, string? ReceivedOn, string? By);

    private async Task<List<MovementCard>> MovementCards(int productId)
    {
        var rows = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderByDescending(m => m.MovedAt).ThenByDescending(m => m.MovementId)
            .Select(m => new
            {
                m.MovementId,
                type = m.MovementType.TypeKey,
                typeName = m.MovementType.TypeName,
                m.MovedAt,
                m.ReferenceNo,
                m.Quantity,
                m.BalanceAfter,
                location = m.Location.LocationName,
                by = m.User.FullName
            })
            .ToListAsync();

        var docs = await ResolveDocuments(rows.Select(r => (r.type, r.ReferenceNo)));

        var transfers = await TransfersByNo(rows
            .Where(r => r.type is TransferOut or TransferIn && r.ReferenceNo != null)
            .Select(r => r.ReferenceNo!).Distinct().ToList());

        var cards = new List<MovementCard>();
        var seenTransfers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            if (r.type is TransferOut or TransferIn && r.ReferenceNo is { } no && transfers.TryGetValue(no, out var t))
            {
                if (!seenTransfers.Add(no)) continue;

                /* Both legs of this transfer for this product. The OUT leg is
                   the card's own id when there is one -- that is the moment the
                   stock left -- and the quantity is what went, not what came:
                   a transfer that has not been received yet still moved stock
                   off the sending shelf. */
                var legs = rows.Where(x => x.ReferenceNo == no && x.type is TransferOut or TransferIn).ToList();
                var outLeg = legs.FirstOrDefault(x => x.type == TransferOut);
                var inLeg = legs.FirstOrDefault(x => x.type == TransferIn);
                var anchor = outLeg ?? inLeg!;

                cards.Add(new MovementCard(
                    Key: $"TRF:{no}",
                    MovementId: anchor.MovementId,
                    Kind: "transfer",
                    Type: "TRANSFER",
                    TypeName: "Transfer",
                    At: anchor.MovedAt,
                    Reference: no,
                    DocumentId: t.Id,
                    DocumentUrl: $"/inventory/transfers/{t.Id}",
                    Direction: "move",
                    Qty: Math.Abs((outLeg ?? inLeg)!.Quantity),
                    Location: null,
                    FromLocation: t.From,
                    ToLocation: t.To,
                    BalanceAfter: null,
                    Status: t.Status,
                    ReceivedOn: t.ReceivedOn?.ToString("yyyy-MM-dd"),
                    By: anchor.by));
                continue;
            }

            docs.TryGetValue((r.type, r.ReferenceNo ?? ""), out var doc);

            cards.Add(new MovementCard(
                Key: $"MV:{r.MovementId}",
                MovementId: r.MovementId,
                Kind: KindOf(r.type),
                Type: r.type,
                TypeName: r.typeName,
                At: r.MovedAt,
                Reference: r.ReferenceNo,
                DocumentId: doc?.Id,
                DocumentUrl: doc?.Url,
                Direction: r.Quantity >= 0 ? "in" : "out",
                Qty: Math.Abs(r.Quantity),
                Location: r.location,
                FromLocation: r.Quantity < 0 ? r.location : null,
                ToLocation: r.Quantity >= 0 ? r.location : null,
                BalanceAfter: r.BalanceAfter,
                Status: null,
                ReceivedOn: null,
                By: r.by));
        }

        return cards;
    }

    private static string KindOf(string typeKey) => typeKey switch
    {
        "PURCHASE" => "purchase",
        "SALE" => "sale",
        "SALE_RETURN" => "sale-return",
        "PURCHASE_RETURN" => "purchase-return",
        "ADJUSTMENT" => "adjustment",
        TransferOut or TransferIn => "transfer",
        _ => "other"
    };

    // ══════════════════════════════════════════════════════════════════
    //  ONE MOVEMENT IN FULL
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Everything about one movement of this product: when, where, who, the
    /// document it belongs to, the other leg if it was a transfer, and what
    /// else was on that document -- so "See complete transfer" is a choice to
    /// see the rest, not the only way to learn what happened.
    /// </summary>
    [HttpGet("movements/{movementId:int}")]
    public async Task<IActionResult> Movement(int productId, int movementId)
    {
        try
        {
            var m = await _db.StockMovements.AsNoTracking()
                .Where(x => x.MovementId == movementId && x.ProductId == productId)
                .Select(x => new
                {
                    id = x.MovementId,
                    type = x.MovementType.TypeKey,
                    typeName = x.MovementType.TypeName,
                    movedAt = x.MovedAt,
                    reference = x.ReferenceNo,
                    qty = x.Quantity,
                    balanceAfter = x.BalanceAfter,
                    locationId = x.LocationId,
                    location = x.Location.LocationName,
                    locationCode = x.Location.LocationCode,
                    city = x.Location.City.CityName,
                    by = x.User.FullName,
                    byRole = x.User.Role.RoleName,
                    product = new
                    {
                        id = x.Product.ProductId,
                        sku = x.Product.Sku,
                        name = x.Product.ProductName,
                        imageUrl = x.Product.ImageUrl,
                        packing = x.Product.Packing,
                        costPrice = x.Product.CostPrice,
                        dutyPrice = x.Product.DutyPrice
                    }
                })
                .FirstOrDefaultAsync();

            if (m is null)
                return NotFound(new { message = $"No movement {movementId} for product {productId}." });

            /* Every leg this product has under the same reference -- for a
               transfer that is the OUT and the IN, for anything else it is just
               this one row. */
            var legs = string.IsNullOrWhiteSpace(m.reference)
                ? new List<object>()
                : (await _db.StockMovements.AsNoTracking()
                    .Where(x => x.ProductId == productId && x.ReferenceNo == m.reference)
                    .OrderBy(x => x.MovedAt).ThenBy(x => x.MovementId)
                    .Select(x => new
                    {
                        id = x.MovementId,
                        type = x.MovementType.TypeKey,
                        typeName = x.MovementType.TypeName,
                        movedAt = x.MovedAt,
                        qty = x.Quantity,
                        balanceAfter = x.BalanceAfter,
                        location = x.Location.LocationName,
                        by = x.User.FullName
                    })
                    .ToListAsync()).Cast<object>().ToList();

            var document = await DocumentFor(m.type, m.reference, productId);

            return Ok(new
            {
                m.id, m.type, m.typeName, m.movedAt, m.reference, m.qty, m.balanceAfter,
                direction = m.type is TransferOut or TransferIn ? "move" : m.qty >= 0 ? "in" : "out",
                m.locationId, m.location, m.locationCode, m.city, m.by, m.byRole,
                /* What the stock on hand was worth moving, at today's landed
                   cost. The movement row does not keep a cost of its own; when
                   the document does (a goods receipt, an invoice) that figure is
                   on `document.thisLine` instead. */
                valueAtCost = Math.Abs(m.qty) * (m.product.costPrice + m.product.dutyPrice),
                m.product,
                legs,
                document
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load movement {movementId}");
        }
    }

    /// <summary>
    /// The document behind a movement, shaped for the detail page: its own
    /// header, this product's line on it, and the rest of its lines.
    /// </summary>
    private async Task<object?> DocumentFor(string type, string? reference, int productId)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        switch (type)
        {
            case TransferOut:
            case TransferIn:
            {
                var t = await _db.StockTransfers.AsNoTracking()
                    .Where(x => x.TransferNo == reference)
                    .Select(x => new
                    {
                        id = x.TransferId,
                        no = x.TransferNo,
                        date = x.TransferDate,
                        receivedOn = x.ReceivedOn,
                        status = x.Status.StatusName,
                        statusKey = x.Status.StatusKey,
                        from = x.FromLocation.LocationName,
                        fromCity = x.FromLocation.City.CityName,
                        to = x.ToLocation.LocationName,
                        toCity = x.ToLocation.City.CityName,
                        initiatedBy = x.InitiatedByUser.User.FullName,
                        approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                        notes = x.Notes,
                        lines = x.StockTransferItems.OrderBy(i => i.LineNo).Select(i => new
                        {
                            productId = i.ProductId,
                            sku = i.Product.Sku,
                            name = i.Product.ProductName,
                            qty = i.Quantity
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (t is null) return null;

                return new
                {
                    kind = "transfer",
                    label = "Stock transfer",
                    t.id, t.no, url = $"/inventory/transfers/{t.id}",
                    completeLabel = "See complete transfer",
                    date = t.date, t.status, t.statusKey,
                    facts = Facts(
                        ("From", $"{t.from} ({t.fromCity})"),
                        ("To", $"{t.to} ({t.toCity})"),
                        ("Sent on", t.date.ToString("dd MMM yyyy")),
                        ("Received on", t.receivedOn?.ToString("dd MMM yyyy") ?? "Not received yet"),
                        ("Initiated by", t.initiatedBy),
                        ("Approved by", t.approvedBy ?? "—"),
                        ("Notes", t.notes)),
                    thisLine = t.lines.Where(l => l.productId == productId)
                        .Select(l => new { qty = l.qty, rate = (decimal?)null, amount = (decimal?)null }).FirstOrDefault(),
                    lineCount = t.lines.Count,
                    totalUnits = t.lines.Sum(l => l.qty),
                    lines = t.lines.Select(l => new
                    {
                        l.productId, l.sku, l.name, l.qty, isThis = l.productId == productId
                    })
                };
            }

            case "PURCHASE":
            {
                var g = await _db.GoodsReceipts.AsNoTracking()
                    .Where(x => x.GrnNo == reference)
                    .Select(x => new
                    {
                        id = x.GrnId,
                        no = x.GrnNo,
                        date = x.ReceiptDate,
                        status = x.Status.StatusName,
                        supplier = x.SupplierUser.LegalName,
                        location = x.Location.LocationName,
                        poNo = x.Po != null ? x.Po.PoNo : null,
                        deliveryNote = x.DeliveryNoteNo,
                        vehicle = x.VehicleNo,
                        receivedBy = x.ReceivedByUser.User.FullName,
                        notes = x.Notes,
                        lines = x.GoodsReceiptItems.OrderBy(i => i.LineNo).Select(i => new
                        {
                            productId = i.ProductId,
                            sku = i.Product.Sku,
                            name = i.Product.ProductName,
                            qty = i.QtyReceived,
                            damaged = i.QtyDamaged,
                            rate = i.UnitCost
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (g is null) return null;

                var mine = g.lines.FirstOrDefault(l => l.productId == productId);
                return new
                {
                    kind = "goods-receipt",
                    label = "Stock received",
                    g.id, g.no, url = $"/purchases/grns/{g.id}",
                    completeLabel = "See complete receipt",
                    date = g.date, g.status, statusKey = (string?)null,
                    facts = Facts(
                        ("Supplier", g.supplier),
                        ("Received at", g.location),
                        ("Against order", g.poNo),
                        ("Delivery note", g.deliveryNote),
                        ("Vehicle", g.vehicle),
                        ("Received by", g.receivedBy),
                        ("Damaged on arrival", mine is { damaged: > 0 } ? $"{mine.damaged} units" : null),
                        ("Notes", g.notes)),
                    thisLine = mine is null ? null : new { mine.qty, rate = (decimal?)mine.rate, amount = (decimal?)(mine.qty * mine.rate) },
                    lineCount = g.lines.Count,
                    totalUnits = g.lines.Sum(l => l.qty),
                    lines = g.lines.Select(l => new { l.productId, l.sku, l.name, l.qty, isThis = l.productId == productId })
                };
            }

            case "SALE":
            {
                var i = await _db.SalesInvoices.AsNoTracking()
                    .Where(x => x.InvoiceNo == reference)
                    .Select(x => new
                    {
                        id = x.InvoiceId,
                        no = x.InvoiceNo,
                        date = x.InvoiceDate,
                        status = x.Status.StatusName,
                        customer = x.IsWalkIn && x.WalkInName != null ? x.WalkInName : x.CustomerUser.LegalName,
                        city = x.IsWalkIn ? null : x.CustomerUser.City.CityName,
                        location = x.Location.LocationName,
                        orderNo = x.Order != null ? x.Order.OrderNo : null,
                        orderId = x.OrderId,
                        rep = x.Order != null && x.Order.SalesPersonUser != null ? x.Order.SalesPersonUser.User.FullName : null,
                        by = x.CreatedByUser.FullName,
                        lines = x.SalesInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                        {
                            productId = l.ProductId,
                            sku = l.Product.Sku,
                            name = l.Product.ProductName,
                            qty = l.Quantity,
                            rate = l.UnitPrice,
                            cost = l.UnitCost,
                            amount = l.LineTotal
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (i is null) return null;

                var mine = i.lines.FirstOrDefault(l => l.productId == productId);
                return new
                {
                    kind = "invoice",
                    label = "Sale invoice",
                    i.id, i.no, url = $"/sales/invoices/{i.id}",
                    completeLabel = "See complete invoice",
                    date = i.date, i.status, statusKey = (string?)null,
                    facts = Facts(
                        ("Customer", i.city is null ? i.customer : $"{i.customer} ({i.city})"),
                        ("Order", i.orderNo ?? "Counter sale"),
                        ("Sold from", i.location),
                        ("Sales rep", i.rep),
                        ("Invoiced by", i.by),
                        ("Unit cost at sale", mine is null ? null : $"PKR {mine.cost:N2}"),
                        ("Profit on this line", mine is null ? null : $"PKR {(mine.rate - mine.cost) * mine.qty:N2}")),
                    thisLine = mine is null ? null : new { mine.qty, rate = (decimal?)mine.rate, amount = (decimal?)mine.amount },
                    lineCount = i.lines.Count,
                    totalUnits = i.lines.Sum(l => l.qty),
                    lines = i.lines.Select(l => new { l.productId, l.sku, l.name, l.qty, isThis = l.productId == productId }),
                    orderUrl = i.orderId is null ? null : $"/sales/orders/{i.orderId}"
                };
            }

            case "SALE_RETURN":
            {
                var r = await _db.SalesReturns.AsNoTracking()
                    .Where(x => x.ReturnNo == reference)
                    .Select(x => new
                    {
                        id = x.ReturnId,
                        no = x.ReturnNo,
                        date = x.ReturnDate,
                        status = x.Status.StatusName,
                        customer = x.CustomerUser.LegalName,
                        invoiceNo = x.Invoice.InvoiceNo,
                        reason = x.Reason,
                        by = x.CreatedByUser.FullName,
                        lines = x.SalesReturnItems.OrderBy(l => l.LineNo).Select(l => new
                        {
                            productId = l.ProductId,
                            sku = l.Product.Sku,
                            name = l.Product.ProductName,
                            qty = l.Quantity,
                            rate = l.UnitPrice,
                            condition = l.Condition.ConditionName
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (r is null) return null;

                var mine = r.lines.FirstOrDefault(l => l.productId == productId);
                return new
                {
                    kind = "sales-return",
                    label = "Sales return",
                    r.id, r.no, url = $"/sales/returns/{r.id}",
                    completeLabel = "See complete return",
                    date = r.date, r.status, statusKey = (string?)null,
                    facts = Facts(
                        ("Customer", r.customer),
                        ("Against invoice", r.invoiceNo),
                        ("Condition", mine?.condition),
                        ("Reason", r.reason),
                        ("Raised by", r.by)),
                    thisLine = mine is null ? null : new { mine.qty, rate = (decimal?)mine.rate, amount = (decimal?)(mine.qty * mine.rate) },
                    lineCount = r.lines.Count,
                    totalUnits = r.lines.Sum(l => l.qty),
                    lines = r.lines.Select(l => new { l.productId, l.sku, l.name, l.qty, isThis = l.productId == productId })
                };
            }

            case "PURCHASE_RETURN":
            {
                var r = await _db.PurchaseReturns.AsNoTracking()
                    .Where(x => x.ReturnNo == reference)
                    .Select(x => new
                    {
                        id = x.PrId,
                        no = x.ReturnNo,
                        date = x.ReturnDate,
                        status = x.Status.StatusName,
                        supplier = x.SupplierUser.LegalName,
                        invoiceNo = x.Pi.InvoiceNo,
                        location = x.Location.LocationName,
                        reason = x.Reason,
                        by = x.CreatedByUser.User.FullName,
                        lines = x.PurchaseReturnItems.OrderBy(l => l.LineNo).Select(l => new
                        {
                            productId = l.ProductId,
                            sku = l.Product.Sku,
                            name = l.Product.ProductName,
                            qty = l.Quantity,
                            rate = l.UnitCost
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (r is null) return null;

                var mine = r.lines.FirstOrDefault(l => l.productId == productId);
                return new
                {
                    kind = "purchase-return",
                    label = "Returned to supplier",
                    r.id, r.no, url = $"/purchases/returns/{r.id}",
                    completeLabel = "See complete return",
                    date = r.date, r.status, statusKey = (string?)null,
                    facts = Facts(
                        ("Supplier", r.supplier),
                        ("Against bill", r.invoiceNo),
                        ("Sent from", r.location),
                        ("Reason", r.reason),
                        ("Raised by", r.by)),
                    thisLine = mine is null ? null : new { mine.qty, rate = (decimal?)mine.rate, amount = (decimal?)(mine.qty * mine.rate) },
                    lineCount = r.lines.Count,
                    totalUnits = r.lines.Sum(l => l.qty),
                    lines = r.lines.Select(l => new { l.productId, l.sku, l.name, l.qty, isThis = l.productId == productId })
                };
            }

            case "ADJUSTMENT":
            {
                var a = await _db.StockAdjustments.AsNoTracking()
                    .Where(x => x.AdjustmentNo == reference)
                    .Select(x => new
                    {
                        id = x.AdjustmentId,
                        no = x.AdjustmentNo,
                        date = x.AdjustmentDate,
                        status = x.Status.StatusName,
                        location = x.Location.LocationName,
                        reason = x.Reason.ReasonName,
                        notes = x.ReasonNotes,
                        by = x.CreatedByUser.User.FullName,
                        lines = x.StockAdjustmentItems.OrderBy(l => l.LineNo).Select(l => new
                        {
                            productId = l.ProductId,
                            sku = l.Product.Sku,
                            name = l.Product.ProductName,
                            before = l.CurrentQty,
                            after = l.NewQty
                        }).ToList()
                    })
                    .FirstOrDefaultAsync();
                if (a is null) return null;

                var mine = a.lines.FirstOrDefault(l => l.productId == productId);
                return new
                {
                    kind = "adjustment",
                    label = "Stock correction",
                    a.id, a.no, url = $"/inventory/adjustments/{a.id}",
                    completeLabel = "See complete correction",
                    date = a.date, a.status, statusKey = (string?)null,
                    facts = Facts(
                        ("Location", a.location),
                        ("Reason", a.reason),
                        ("Counted", mine is null ? null : $"{mine.before} on record, {mine.after} on the shelf"),
                        ("Notes", a.notes),
                        ("Corrected by", a.by)),
                    thisLine = mine is null ? null : new { qty = mine.after - mine.before, rate = (decimal?)null, amount = (decimal?)null },
                    lineCount = a.lines.Count,
                    totalUnits = a.lines.Sum(l => l.after - l.before),
                    lines = a.lines.Select(l => new { l.productId, l.sku, l.name, qty = l.after - l.before, isThis = l.productId == productId })
                };
            }

            default:
                return null;
        }
    }

    /// <summary>Label/value pairs with the empty ones dropped, so the card never prints "Vehicle: —".</summary>
    private static List<object> Facts(params (string Label, string? Value)[] pairs) =>
        pairs.Where(p => !string.IsNullOrWhiteSpace(p.Value))
             .Select(p => (object)new { label = p.Label, value = p.Value })
             .ToList();

    // ══════════════════════════════════════════════════════════════════
    //  DOCUMENT LOOKUPS SHARED BY THE CARDS AND THE LEDGER
    // ══════════════════════════════════════════════════════════════════

    private sealed record DocRef(int Id, string Url, decimal? Rate);

    /// <summary>
    /// For each (movement type, reference) pair, the document it points at.
    /// One query per document kind, never one per row.
    /// </summary>
    private async Task<Dictionary<(string, string), DocRef>> ResolveDocuments(
        IEnumerable<(string Type, string? Reference)> pairs, int? productId = null)
    {
        var list = pairs.Where(p => !string.IsNullOrWhiteSpace(p.Reference))
            .Select(p => (p.Type, Reference: p.Reference!)).Distinct().ToList();
        var result = new Dictionary<(string, string), DocRef>();

        List<string> Refs(params string[] types) =>
            list.Where(p => types.Contains(p.Type)).Select(p => p.Reference).Distinct().ToList();

        var grn = Refs("PURCHASE");
        if (grn.Count > 0)
        {
            var found = await _db.GoodsReceipts.AsNoTracking()
                .Where(g => grn.Contains(g.GrnNo))
                .Select(g => new
                {
                    g.GrnId, g.GrnNo,
                    rate = productId == null ? null
                        : g.GoodsReceiptItems.Where(i => i.ProductId == productId).Select(i => (decimal?)i.UnitCost).FirstOrDefault()
                })
                .ToListAsync();
            foreach (var g in found) result[("PURCHASE", g.GrnNo)] = new DocRef(g.GrnId, $"/purchases/grns/{g.GrnId}", g.rate);
        }

        var inv = Refs("SALE");
        if (inv.Count > 0)
        {
            var found = await _db.SalesInvoices.AsNoTracking()
                .Where(i => inv.Contains(i.InvoiceNo))
                .Select(i => new
                {
                    i.InvoiceId, i.InvoiceNo,
                    rate = productId == null ? null
                        : i.SalesInvoiceItems.Where(l => l.ProductId == productId).Select(l => (decimal?)l.UnitCost).FirstOrDefault()
                })
                .ToListAsync();
            foreach (var i in found) result[("SALE", i.InvoiceNo)] = new DocRef(i.InvoiceId, $"/sales/invoices/{i.InvoiceId}", i.rate);
        }

        var sr = Refs("SALE_RETURN");
        if (sr.Count > 0)
            foreach (var r in await _db.SalesReturns.AsNoTracking().Where(r => sr.Contains(r.ReturnNo))
                         .Select(r => new { r.ReturnId, r.ReturnNo }).ToListAsync())
                result[("SALE_RETURN", r.ReturnNo)] = new DocRef(r.ReturnId, $"/sales/returns/{r.ReturnId}", null);

        var pr = Refs("PURCHASE_RETURN");
        if (pr.Count > 0)
            foreach (var r in await _db.PurchaseReturns.AsNoTracking().Where(r => pr.Contains(r.ReturnNo))
                         .Select(r => new { r.PrId, r.ReturnNo }).ToListAsync())
                result[("PURCHASE_RETURN", r.ReturnNo)] = new DocRef(r.PrId, $"/purchases/returns/{r.PrId}", null);

        var adj = Refs("ADJUSTMENT");
        if (adj.Count > 0)
            foreach (var a in await _db.StockAdjustments.AsNoTracking().Where(a => adj.Contains(a.AdjustmentNo))
                         .Select(a => new { a.AdjustmentId, a.AdjustmentNo }).ToListAsync())
                result[("ADJUSTMENT", a.AdjustmentNo)] = new DocRef(a.AdjustmentId, $"/inventory/adjustments/{a.AdjustmentId}", null);

        var trf = Refs(TransferOut, TransferIn);
        if (trf.Count > 0)
            foreach (var t in await _db.StockTransfers.AsNoTracking().Where(t => trf.Contains(t.TransferNo))
                         .Select(t => new { t.TransferId, t.TransferNo }).ToListAsync())
            {
                result[(TransferOut, t.TransferNo)] = new DocRef(t.TransferId, $"/inventory/transfers/{t.TransferId}", null);
                result[(TransferIn, t.TransferNo)] = new DocRef(t.TransferId, $"/inventory/transfers/{t.TransferId}", null);
            }

        return result;
    }

    private sealed record TransferHead(int Id, string From, string To, string Status, DateOnly Date, DateOnly? ReceivedOn);

    private async Task<Dictionary<string, TransferHead>> TransfersByNo(List<string> numbers)
    {
        if (numbers.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        var rows = await _db.StockTransfers.AsNoTracking()
            .Where(t => numbers.Contains(t.TransferNo))
            .Select(t => new
            {
                t.TransferNo,
                head = new TransferHead(t.TransferId, t.FromLocation.LocationName, t.ToLocation.LocationName,
                    t.Status.StatusName, t.TransferDate, t.ReceivedOn)
            })
            .ToListAsync();

        return rows.ToDictionary(r => r.TransferNo, r => r.head, StringComparer.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════
    //  HISTORY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>One line of a product's life.</summary>
    public sealed record HistoryEvent(
        string Key, DateTime At, bool HasTime, string Kind, string Group, string Title,
        string? Reference, string? Url, int? Qty, string Direction,
        string? Location, string? From, string? To, string? Party,
        decimal? Rate, decimal? Amount, string? By, string? Status, string? Detail);

    private static readonly (string Key, string Label)[] Groups =
    {
        ("purchasing", "Purchasing"),
        ("sales", "Orders & sales"),
        ("journey", "Order journey"),
        ("delivery", "Dispatch & delivery"),
        ("returns", "Returns"),
        ("transfers", "Transfers"),
        ("adjustments", "Stock corrections"),
        ("claims", "Claims"),
        ("catalogue", "Catalogue"),
    };

    /// <summary>
    /// The product's whole life as one timeline, newest first, with a summary
    /// and the counts per group for the filter chips.
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> History(int productId, [FromQuery] string? group,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 100) pageSize = 30;

            var head = await ProductHead(productId);
            if (head is null) return NotFound(new { message = $"No product with id {productId}." });

            var events = await BuildHistory(productId, head.Sku);
            var summary = await Summary(productId, head);

            var groups = Groups
                .Select(g => new { key = g.Key, label = g.Label, count = events.Count(e => e.Group == g.Key) })
                .Where(g => g.count > 0)
                .ToList();

            if (!string.IsNullOrWhiteSpace(group))
                events = events.Where(e => e.Group.Equals(group, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = events.Count;
            var items = events.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new { product = head, summary, groups, total, page, pageSize, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the history of product {productId}");
        }
    }

    public sealed record Head(
        int Id, string Sku, string Name, string? ImageUrl, string Category, string Brand,
        int Packing, decimal CostPrice, decimal DutyPrice, decimal MarginPrice, decimal SalePrice,
        DateOnly CreatedAt, bool IsActive);

    private Task<Head?> ProductHead(int productId) =>
        _db.Products.AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(p => new Head(p.ProductId, p.Sku, p.ProductName, p.ImageUrl,
                p.Category.CategoryName, p.Brand.BrandName, p.Packing,
                p.CostPrice, p.DutyPrice, p.SalePrice - p.CostPrice - p.DutyPrice, p.SalePrice, p.CreatedAt, p.IsActive))
            .FirstOrDefaultAsync();

    private static DateTime Day(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    /// <summary>
    /// Every event, from every document that mentions this product. Sorted
    /// newest first; within one day, the order the steps really happen in.
    /// </summary>
    private async Task<List<HistoryEvent>> BuildHistory(int productId, string sku)
    {
        var ev = new List<HistoryEvent>();

        /* ── purchasing ─────────────────────────────────────────────── */

        var pos = await _db.PurchaseOrderItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Po.PoId, no = i.Po.PoNo, date = i.Po.PoDate, expected = i.Po.ExpectedDate,
                supplier = i.Po.SupplierUser.LegalName, location = i.Po.Location.LocationName,
                status = i.Po.Status.StatusName, by = i.Po.CreatedByUser.User.FullName,
                i.Quantity, i.UnitCost, i.LineTotal
            })
            .ToListAsync();
        ev.AddRange(pos.Select(x => new HistoryEvent(
            $"PO:{x.id}", Day(x.date), false, "purchase-order", "purchasing", "Ordered from supplier",
            x.no, $"/purchases/orders/{x.id}", x.Quantity, "none",
            x.location, null, x.location, x.supplier, x.UnitCost, x.LineTotal, x.by, x.status,
            x.expected is null ? null : $"Expected {x.expected:dd MMM yyyy}")));

        var grns = await _db.GoodsReceiptItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Grn.GrnId, no = i.Grn.GrnNo, date = i.Grn.ReceiptDate,
                supplier = i.Grn.SupplierUser.LegalName, location = i.Grn.Location.LocationName,
                status = i.Grn.Status.StatusName, by = i.Grn.ReceivedByUser.User.FullName,
                poNo = i.Grn.Po != null ? i.Grn.Po.PoNo : null,
                i.QtyReceived, i.QtyDamaged, i.UnitCost, i.BatchNo
            })
            .ToListAsync();
        ev.AddRange(grns.Select(x => new HistoryEvent(
            $"GRN:{x.id}", Day(x.date), false, "goods-receipt", "purchasing", "Stock received from supplier",
            x.no, $"/purchases/grns/{x.id}", x.QtyReceived, "in",
            x.location, null, x.location, x.supplier, x.UnitCost, x.QtyReceived * x.UnitCost, x.by, x.status,
            Join(x.poNo is null ? null : $"Against {x.poNo}",
                 x.QtyDamaged > 0 ? $"{x.QtyDamaged} damaged on arrival" : null,
                 x.BatchNo is null ? null : $"Batch {x.BatchNo}"))));

        var pis = await _db.PurchaseInvoiceItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Pi.PiId, no = i.Pi.InvoiceNo, supplierNo = i.Pi.SupplierInvoiceNo, date = i.Pi.InvoiceDate,
                due = i.Pi.DueDate, supplier = i.Pi.SupplierUser.LegalName, status = i.Pi.Status.StatusName,
                by = i.Pi.CreatedByUser.User.FullName, i.Quantity, i.UnitCost, i.LineTotal
            })
            .ToListAsync();
        ev.AddRange(pis.Select(x => new HistoryEvent(
            $"PI:{x.id}", Day(x.date), false, "purchase-invoice", "purchasing", "Supplier billed us",
            x.no, $"/purchases/invoices/{x.id}", x.Quantity, "none",
            null, null, null, x.supplier, x.UnitCost, x.LineTotal, x.by, x.status,
            Join($"Supplier's bill {x.supplierNo}", $"Due {x.due:dd MMM yyyy}"))));

        var prs = await _db.PurchaseReturnItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Pr.PrId, no = i.Pr.ReturnNo, date = i.Pr.ReturnDate,
                supplier = i.Pr.SupplierUser.LegalName, location = i.Pr.Location.LocationName,
                status = i.Pr.Status.StatusName, by = i.Pr.CreatedByUser.User.FullName, reason = i.Pr.Reason,
                i.Quantity, i.UnitCost
            })
            .ToListAsync();
        ev.AddRange(prs.Select(x => new HistoryEvent(
            $"PR:{x.id}", Day(x.date), false, "purchase-return", "returns", "Returned to supplier",
            x.no, $"/purchases/returns/{x.id}", x.Quantity, "out",
            x.location, x.location, null, x.supplier, x.UnitCost, x.Quantity * x.UnitCost, x.by, x.status, x.reason)));

        /* ── orders, invoices and the journey in between ───────────── */

        var orders = await _db.SalesOrderItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Order.OrderId, no = i.Order.OrderNo, date = i.Order.OrderDate,
                customer = i.Order.CustomerUser.LegalName, city = i.Order.CustomerUser.City.CityName,
                location = i.Order.Location.LocationName, status = i.Order.Status.StatusName,
                rep = i.Order.SalesPersonUser != null ? i.Order.SalesPersonUser.User.FullName : i.Order.CreatedByUser.FullName,
                i.Quantity, i.UnitPrice, i.LineTotal, i.DiscountPercent
            })
            .ToListAsync();
        ev.AddRange(orders.Select(x => new HistoryEvent(
            $"SO:{x.id}", Day(x.date), false, "sales-order", "sales", "Customer ordered",
            x.no, $"/sales/orders/{x.id}", x.Quantity, "none",
            x.location, x.location, null, $"{x.customer} ({x.city})", x.UnitPrice, x.LineTotal, x.rep, x.status,
            x.DiscountPercent > 0 ? $"{x.DiscountPercent:0.##}% discount" : null)));

        /* The order's own journey -- confirmed, invoiced, picked up by the
           warehouse, on its way to the order desk, packed, dispatched,
           delivered -- is in the activity log, one row per step, written by
           the status endpoint. Read for exactly the orders this product is on. */
        var orderNos = orders.Select(o => o.no).Distinct().ToList();
        var orderByNo = orders.GroupBy(o => o.no).ToDictionary(g => g.Key, g => g.First());
        if (orderNos.Count > 0)
        {
            var steps = await _db.ActivityLogs.AsNoTracking()
                .Where(a => a.EntityType == "SalesOrder" && orderNos.Contains(a.EntityReference)
                         && (a.ActionName == "ORDER_STATUS_CHANGED" || a.ActionName == "ORDER_CANCELLED"
                             || a.ActionName == "ORDER_INVOICED" || a.ActionName == "ORDER_STATUS_RESTORED"))
                .Select(a => new
                {
                    a.LogId, a.EntityReference, a.ActionName, a.Detail, a.LoggedAt,
                    by = a.User != null ? a.User.FullName : "System"
                })
                .ToListAsync();

            foreach (var s in steps)
            {
                var o = orderByNo[s.EntityReference];
                var (title, status) = StepTitle(s.ActionName, s.Detail);
                ev.Add(new HistoryEvent(
                    $"LOG:{s.LogId}", s.LoggedAt, true, "order-step", "journey", title,
                    o.no, $"/sales/orders/{o.id}", o.Quantity, "none",
                    null, null, null, $"{o.customer} ({o.city})", null, null, s.by, status, s.Detail));
            }
        }

        var invoices = await _db.SalesInvoiceItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Invoice.InvoiceId, no = i.Invoice.InvoiceNo, date = i.Invoice.InvoiceDate,
                customer = i.Invoice.IsWalkIn && i.Invoice.WalkInName != null ? i.Invoice.WalkInName : i.Invoice.CustomerUser.LegalName,
                location = i.Invoice.Location.LocationName, status = i.Invoice.Status.StatusName,
                orderNo = i.Invoice.Order != null ? i.Invoice.Order.OrderNo : null,
                by = i.Invoice.CreatedByUser.FullName, walkIn = i.Invoice.IsWalkIn,
                i.Quantity, i.UnitPrice, i.UnitCost, i.LineTotal
            })
            .ToListAsync();
        ev.AddRange(invoices.Select(x => new HistoryEvent(
            $"INV:{x.id}", Day(x.date), false, "invoice", "sales", x.walkIn ? "Sold over the counter" : "Invoiced to customer",
            x.no, $"/sales/invoices/{x.id}", x.Quantity, "out",
            x.location, x.location, null, x.customer, x.UnitPrice, x.LineTotal, x.by, x.status,
            Join(x.orderNo is null ? null : $"Order {x.orderNo}",
                 $"Cost {x.UnitCost:N2}, profit {(x.UnitPrice - x.UnitCost) * x.Quantity:N2}"))));

        /* ── dispatch and delivery ──────────────────────────────────── */

        var orderIds = orders.Select(o => o.id).Distinct().ToList();
        if (orderIds.Count > 0)
        {
            var deliveries = await _db.Deliveries.AsNoTracking()
                .Where(d => orderIds.Contains(d.OrderId))
                .Select(d => new
                {
                    d.DeliveryId, d.DeliveryNo, d.OrderId, d.BookedDate, d.DeliveredDate, d.ExpectedDate,
                    d.TrackingNo, d.Parcels,
                    channel = d.Channel.ChannelName,
                    courier = d.Courier != null ? d.Courier.CourierName : null,
                    status = d.Status.StatusName,
                    confirmedBy = d.ConfirmedByUser != null ? d.ConfirmedByUser.User.FullName : null
                })
                .ToListAsync();

            foreach (var d in deliveries)
            {
                var o = orders.First(x => x.id == d.OrderId);
                var how = Join(d.channel, d.courier, d.TrackingNo is null ? null : $"Tracking {d.TrackingNo}",
                    d.Parcels > 0 ? $"{d.Parcels} parcel{(d.Parcels == 1 ? "" : "s")}" : null);

                ev.Add(new HistoryEvent(
                    $"DLV:{d.DeliveryId}:out", Day(d.BookedDate), false, "dispatch", "delivery",
                    "Dispatched from the order department",
                    o.no, $"/sales/orders/{o.id}", o.Quantity, "none",
                    o.location, o.location, o.city, $"{o.customer} ({o.city})", null, null, null, d.status,
                    Join(d.DeliveryNo, how, d.ExpectedDate is null ? null : $"Expected {d.ExpectedDate:dd MMM yyyy}")));

                if (d.DeliveredDate is { } delivered)
                    ev.Add(new HistoryEvent(
                        $"DLV:{d.DeliveryId}:in", Day(delivered), false, "delivered", "delivery",
                        "Delivered to the customer",
                        o.no, $"/sales/orders/{o.id}", o.Quantity, "none",
                        null, null, o.city, $"{o.customer} ({o.city})", null, null, d.confirmedBy, d.status,
                        Join(d.DeliveryNo, $"{(delivered.DayNumber - d.BookedDate.DayNumber)} days after dispatch")));
            }
        }

        /* ── customer returns ───────────────────────────────────────── */

        var sreturns = await _db.SalesReturnItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Return.ReturnId, no = i.Return.ReturnNo, date = i.Return.ReturnDate,
                customer = i.Return.CustomerUser.LegalName, invoiceNo = i.Return.Invoice.InvoiceNo,
                status = i.Return.Status.StatusName, by = i.Return.CreatedByUser.FullName,
                reason = i.Return.Reason, condition = i.Condition.ConditionName, resalable = i.Condition.IsResalable,
                back = i.RestockLocation != null ? i.RestockLocation.LocationName : null,
                i.Quantity, i.UnitPrice
            })
            .ToListAsync();
        ev.AddRange(sreturns.Select(x => new HistoryEvent(
            $"SR:{x.id}", Day(x.date), false, "sales-return", "returns", "Returned by the customer",
            x.no, $"/sales/returns/{x.id}", x.Quantity, x.resalable ? "in" : "none",
            x.back, null, x.back, x.customer, x.UnitPrice, x.Quantity * x.UnitPrice, x.by, x.status,
            Join($"Against {x.invoiceNo}", x.condition, x.resalable ? $"Back on the shelf at {x.back}" : "Written off", x.reason))));

        /* ── transfers ──────────────────────────────────────────────── */

        var transfers = await _db.StockTransferItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Transfer.TransferId, no = i.Transfer.TransferNo, date = i.Transfer.TransferDate,
                received = i.Transfer.ReceivedOn,
                from = i.Transfer.FromLocation.LocationName, to = i.Transfer.ToLocation.LocationName,
                status = i.Transfer.Status.StatusName, by = i.Transfer.InitiatedByUser.User.FullName,
                i.Quantity
            })
            .ToListAsync();
        ev.AddRange(transfers.Select(x => new HistoryEvent(
            $"TRF:{x.id}", Day(x.date), false, "transfer", "transfers", "Moved between locations",
            x.no, $"/inventory/transfers/{x.id}", x.Quantity, "move",
            null, x.from, x.to, null, null, null, x.by, x.status,
            x.received is null ? "Not received yet" : $"Received {x.received:dd MMM yyyy}")));

        /* ── stock corrections ──────────────────────────────────────── */

        var adjustments = await _db.StockAdjustmentItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .Select(i => new
            {
                id = i.Adjustment.AdjustmentId, no = i.Adjustment.AdjustmentNo, date = i.Adjustment.AdjustmentDate,
                location = i.Adjustment.Location.LocationName, reason = i.Adjustment.Reason.ReasonName,
                notes = i.Adjustment.ReasonNotes, status = i.Adjustment.Status.StatusName,
                by = i.Adjustment.CreatedByUser.User.FullName, i.CurrentQty, i.NewQty
            })
            .ToListAsync();
        ev.AddRange(adjustments.Select(x => new HistoryEvent(
            $"ADJ:{x.id}", Day(x.date), false, "adjustment", "adjustments", "Stock count corrected",
            x.no, $"/inventory/adjustments/{x.id}", Math.Abs(x.NewQty - x.CurrentQty),
            x.NewQty >= x.CurrentQty ? "in" : "out",
            x.location, null, null, null, null, null, x.by, x.status,
            Join($"{x.CurrentQty} on record, {x.NewQty} counted", x.reason, x.notes))));

        /* ── warranty claims ────────────────────────────────────────── */

        var claims = await _db.Claims.AsNoTracking()
            .Where(c => c.ProductId == productId)
            .Select(c => new
            {
                c.ClaimId, c.ClaimNo, c.ReceivedOn, c.SentOn, c.SettledOn, c.Quantity, c.UnitCost,
                customer = c.CustomerUser.LegalName, supplier = c.SupplierUser != null ? c.SupplierUser.LegalName : null,
                stage = c.Stage.StageName, outcome = c.Outcome.OutcomeName, reason = c.Reason.ReasonName,
                by = c.ReceivedByUser.User.FullName
            })
            .ToListAsync();
        ev.AddRange(claims.Select(x => new HistoryEvent(
            $"CLM:{x.ClaimId}", Day(x.ReceivedOn), false, "claim", "claims", "Warranty claim received",
            x.ClaimNo, $"/claims/{x.ClaimId}", x.Quantity, "none",
            null, null, null, x.customer, x.UnitCost, x.Quantity * x.UnitCost, x.by, x.stage,
            Join(x.reason, x.supplier is null ? null : $"Sent to {x.supplier}",
                 x.SentOn is null ? null : $"sent {x.SentOn:dd MMM yyyy}",
                 x.SettledOn is null ? null : $"settled {x.SettledOn:dd MMM yyyy}: {x.outcome}"))));

        /* ── the catalogue entry itself ─────────────────────────────── */

        var catalogue = await _db.ActivityLogs.AsNoTracking()
            .Where(a => a.EntityType == "Product" && a.EntityReference == sku)
            .Select(a => new { a.LogId, a.ActionName, a.Detail, a.LoggedAt, by = a.User != null ? a.User.FullName : "System" })
            .ToListAsync();
        ev.AddRange(catalogue.Select(a => new HistoryEvent(
            $"LOG:{a.LogId}", a.LoggedAt, true, "catalogue", "catalogue",
            a.ActionName == "PRODUCT_CREATED" ? "Added to the catalogue" : "Product details edited",
            sku, null, null, "none", null, null, null, null, null, null, a.by, null, a.Detail)));

        return ev
            .OrderByDescending(e => e.At.Date)
            .ThenByDescending(e => DayRank(e.Kind))
            .ThenByDescending(e => e.At)
            .ToList();
    }

    /// <summary>
    /// Within a single day, the order things really happen in. A document dated
    /// "today" has no time on it, so without this an order would appear to be
    /// delivered before it was taken.
    /// </summary>
    private static int DayRank(string kind) => kind switch
    {
        "catalogue" => 0,
        "purchase-order" => 1,
        "goods-receipt" => 2,
        "purchase-invoice" => 3,
        "sales-order" => 4,
        "order-step" => 5,
        "invoice" => 6,
        "transfer" => 7,
        "dispatch" => 8,
        "delivered" => 9,
        "adjustment" => 10,
        "sales-return" => 11,
        "purchase-return" => 12,
        "claim" => 13,
        _ => 14
    };

    /// <summary>"Packaging -> Dispatched" becomes "Order dispatched".</summary>
    private static (string Title, string? Status) StepTitle(string action, string? detail)
    {
        if (action == "ORDER_CANCELLED") return ("Order cancelled", "Cancelled");
        if (action == "ORDER_INVOICED") return ("Order invoiced", "Invoiced");

        var to = detail?.Split("->").LastOrDefault()?.Split('.').FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(to)) return ("Order status changed", null);

        var title = to.ToLowerInvariant() switch
        {
            "submitted" => "Order sent in by the rep",
            "confirmed" => "Order confirmed by the owner",
            "declined" => "Order declined",
            "invoiced" => "Order invoiced",
            "seen by warehouse" => "Picked up by the warehouse",
            "on way to order dept" => "On its way to the order department",
            "received at order dept" => "Received at the order department",
            "packaging" => "Packed at the order department",
            "dispatched" => "Dispatched from the order department",
            "delivered" => "Delivered to the customer",
            "cancelled" => "Order cancelled",
            _ => $"Order moved to {to}"
        };

        return (action == "ORDER_STATUS_RESTORED" ? $"{title} (status restored)" : title, to);
    }

    private static string? Join(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return kept.Count == 0 ? null : string.Join(" · ", kept);
    }

    // ══════════════════════════════════════════════════════════════════
    //  SUMMARY
    // ══════════════════════════════════════════════════════════════════

    private async Task<object> Summary(int productId, Head head)
    {
        var stock = await _db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId)
            .Select(s => new { s.Quantity, location = s.Location.LocationName, city = s.Location.City.CityName })
            .ToListAsync();

        var received = await _db.GoodsReceiptItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                units = g.Sum(i => i.QtyReceived),
                damaged = g.Sum(i => i.QtyDamaged),
                value = g.Sum(i => i.QtyReceived * i.UnitCost),
                last = g.Max(i => i.Grn.ReceiptDate)
            })
            .FirstOrDefaultAsync();

        var sold = await _db.SalesInvoiceItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                units = g.Sum(i => i.Quantity),
                net = g.Sum(i => i.Quantity * i.UnitPrice * (1 - i.DiscountPercent / 100m)),
                billed = g.Sum(i => i.LineTotal),
                cost = g.Sum(i => i.Quantity * i.UnitCost),
                invoices = g.Select(i => i.InvoiceId).Distinct().Count(),
                customers = g.Select(i => i.Invoice.CustomerUserId).Distinct().Count(),
                first = g.Min(i => i.Invoice.InvoiceDate),
                last = g.Max(i => i.Invoice.InvoiceDate)
            })
            .FirstOrDefaultAsync();

        var ordered = await _db.SalesOrderItems.AsNoTracking()
            .Where(i => i.ProductId == productId && i.Order.Status.StatusKey != "CANCELLED" && i.Order.Status.StatusKey != "DECLINED")
            .SumAsync(i => (int?)i.Quantity) ?? 0;

        var returnedIn = await _db.SalesReturnItems.AsNoTracking()
            .Where(i => i.ProductId == productId && i.Return.Status.StatusKey != "REJECTED")
            .SumAsync(i => (int?)i.Quantity) ?? 0;

        var returnedOut = await _db.PurchaseReturnItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .SumAsync(i => (int?)i.Quantity) ?? 0;

        var transferred = await _db.StockTransferItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .GroupBy(_ => 1)
            .Select(g => new { count = g.Select(i => i.TransferId).Distinct().Count(), units = g.Sum(i => i.Quantity) })
            .FirstOrDefaultAsync();

        var corrected = await _db.StockAdjustmentItems.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .SumAsync(i => (int?)(i.NewQty - i.CurrentQty)) ?? 0;

        var claims = await _db.Claims.AsNoTracking().CountAsync(c => c.ProductId == productId);

        var onHand = stock.Sum(s => s.Quantity);
        var landed = head.CostPrice + head.DutyPrice;
        var soldUnits = sold?.units ?? 0;

        return new
        {
            onHand,
            stockValue = onHand * landed,
            byLocation = stock.Where(s => s.Quantity != 0)
                .Select(s => new { s.location, s.city, qty = s.Quantity }).ToList(),

            purchasedUnits = received?.units ?? 0,
            purchasedValue = received?.value ?? 0m,
            damagedOnArrival = received?.damaged ?? 0,
            lastPurchasedOn = received?.last,

            orderedUnits = ordered,
            soldUnits,
            invoiceCount = sold?.invoices ?? 0,
            customerCount = sold?.customers ?? 0,
            netSales = Math.Round(sold?.net ?? 0m, 2),
            billedWithTax = sold?.billed ?? 0m,
            costOfSales = sold?.cost ?? 0m,
            grossProfit = Math.Round((sold?.net ?? 0m) - (sold?.cost ?? 0m), 2),
            averageSellingPrice = soldUnits > 0 ? Math.Round((sold!.net) / soldUnits, 2) : 0m,
            firstSoldOn = sold?.first,
            lastSoldOn = sold?.last,

            returnedByCustomers = returnedIn,
            returnedToSuppliers = returnedOut,
            transfers = transferred?.count ?? 0,
            unitsTransferred = transferred?.units ?? 0,
            netCorrection = corrected,
            claims
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE STOCK LEDGER
    // ══════════════════════════════════════════════════════════════════

    public sealed record LedgerRow(
        int MovementId, DateTime At, string Type, string TypeName, string? Reference, string? Url,
        string Location, int QtyIn, int QtyOut, int BalanceAtLocation, int RunningTotal,
        decimal? Rate, decimal? Value, string? By);

    /// <summary>
    /// The stock card: what came in, what went out, and the balance after each.
    ///
    /// THE OPENING LINE IS NOT A FUDGE. Some stock was on the shelves before
    /// anything in this system recorded it moving -- the seed catalogue came in
    /// with balances and no history. Opening + movements = on hand is the
    /// ledger's own equation, so the opening is whatever makes it true, and it
    /// is shown as its own line rather than folded silently into the first
    /// movement.
    /// </summary>
    [HttpGet("ledger")]
    public async Task<IActionResult> Ledger(int productId, [FromQuery] int page = 1, [FromQuery] int pageSize = 30)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 30;

            if (!await _db.Products.AnyAsync(p => p.ProductId == productId))
                return NotFound(new { message = $"No product with id {productId}." });

            var (opening, onHand, rows) = await BuildLedger(productId);

            /* Newest first on screen, the running total already worked out
               oldest-first so every row carries the balance it actually had. */
            var ordered = rows.AsEnumerable().Reverse().ToList();
            var total = ordered.Count;
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            return Ok(new
            {
                opening,
                totalIn = rows.Sum(r => r.QtyIn),
                totalOut = rows.Sum(r => r.QtyOut),
                onHand,
                total, page, pageSize, items
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load the stock ledger of product {productId}");
        }
    }

    private async Task<(int Opening, int OnHand, List<LedgerRow> Rows)> BuildLedger(int productId)
    {
        var moves = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == productId)
            .OrderBy(m => m.MovedAt).ThenBy(m => m.MovementId)
            .Select(m => new
            {
                m.MovementId, m.MovedAt, type = m.MovementType.TypeKey, typeName = m.MovementType.TypeName,
                m.ReferenceNo, m.Quantity, m.BalanceAfter, location = m.Location.LocationName, by = m.User.FullName
            })
            .ToListAsync();

        var onHand = await _db.StockBalances.AsNoTracking()
            .Where(s => s.ProductId == productId).SumAsync(s => (int?)s.Quantity) ?? 0;

        var docs = await ResolveDocuments(moves.Select(m => (m.type, m.ReferenceNo)), productId);

        var opening = onHand - moves.Sum(m => m.Quantity);
        var running = opening;
        var rows = new List<LedgerRow>(moves.Count);

        foreach (var m in moves)
        {
            running += m.Quantity;
            docs.TryGetValue((m.type, m.ReferenceNo ?? ""), out var doc);
            var qty = Math.Abs(m.Quantity);

            rows.Add(new LedgerRow(
                m.MovementId, m.MovedAt, m.type, m.typeName, m.ReferenceNo, doc?.Url,
                m.location,
                QtyIn: m.Quantity > 0 ? m.Quantity : 0,
                QtyOut: m.Quantity < 0 ? -m.Quantity : 0,
                BalanceAtLocation: m.BalanceAfter,
                RunningTotal: running,
                Rate: doc?.Rate,
                Value: doc?.Rate is { } r ? qty * r : null,
                By: m.by));
        }

        return (opening, onHand, rows);
    }

    // ══════════════════════════════════════════════════════════════════
    //  EXPORT
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The whole history as one workbook: a summary, the timeline, the stock
    /// ledger, and the purchase and sale lines on their own, each on its own
    /// tab. Everything, not the page on screen -- an export that stopped at
    /// thirty rows would be a screenshot.
    /// </summary>
    [HttpGet("history/export")]
    public async Task<IActionResult> Export(int productId)
    {
        try
        {
            var head = await ProductHead(productId);
            if (head is null) return NotFound(new { message = $"No product with id {productId}." });

            var events = await BuildHistory(productId, head.Sku);
            var summary = await Summary(productId, head);
            var (opening, onHand, ledger) = await BuildLedger(productId);

            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            JsonElement J(object o) => JsonSerializer.SerializeToElement(o, opts);

            /* Summary as label/value rows: a workbook tab reads down, not across. */
            var s = J(summary);
            var summaryRows = new List<object>
            {
                new { label = "Product", value = head.Name },
                new { label = "SKU", value = head.Sku },
                new { label = "Category", value = head.Category },
                new { label = "Brand", value = head.Brand },
                new { label = "Cost price", value = head.CostPrice.ToString("N2") },
                new { label = "Duty", value = head.DutyPrice.ToString("N2") },
                new { label = "Margin", value = head.MarginPrice.ToString("N2") },
                new { label = "Sale price", value = head.SalePrice.ToString("N2") },
                new { label = "In the catalogue since", value = head.CreatedAt.ToString("dd MMM yyyy") },
            };
            foreach (var (label, field) in new[]
                     {
                         ("On hand", "onHand"), ("Stock value at landed cost", "stockValue"),
                         ("Units received from suppliers", "purchasedUnits"), ("Value received", "purchasedValue"),
                         ("Damaged on arrival", "damagedOnArrival"), ("Units ordered by customers", "orderedUnits"),
                         ("Units sold", "soldUnits"), ("Invoices", "invoiceCount"), ("Customers", "customerCount"),
                         ("Net sales (excl. tax)", "netSales"), ("Billed including tax", "billedWithTax"),
                         ("Cost of sales", "costOfSales"), ("Gross profit", "grossProfit"),
                         ("Average selling price", "averageSellingPrice"),
                         ("Returned by customers", "returnedByCustomers"), ("Returned to suppliers", "returnedToSuppliers"),
                         ("Transfers", "transfers"), ("Units transferred", "unitsTransferred"),
                         ("Net stock correction", "netCorrection"), ("Warranty claims", "claims"),
                     })
            {
                s.TryGetProperty(field, out var v);
                summaryRows.Add(new { label, value = v.ValueKind == JsonValueKind.Undefined ? "" : v.ToString() });
            }
            summaryRows.Add(new { label = "Opening stock (before records began)", value = opening.ToString() });

            var sheets = new List<XlsxWriter.SheetSpec>
            {
                new("Summary", new[]
                {
                    new XlsxWriter.Column("Figure", "label", XlsxWriter.CellKind.Text, 38),
                    new XlsxWriter.Column("Value", "value", XlsxWriter.CellKind.Text, 40),
                }, J(summaryRows)),

                new("Timeline", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Event", "title", XlsxWriter.CellKind.Text, 34),
                    new XlsxWriter.Column("Group", "group", XlsxWriter.CellKind.Text, 14),
                    new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Qty", "qty", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("In / Out", "direction", XlsxWriter.CellKind.Text, 9),
                    new XlsxWriter.Column("Customer / Supplier", "party", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("From", "from", XlsxWriter.CellKind.Text, 22),
                    new XlsxWriter.Column("To", "to", XlsxWriter.CellKind.Text, 22),
                    new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 22),
                    new XlsxWriter.Column("Rate", "rate", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Amount", "amount", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Status", "status", XlsxWriter.CellKind.Text, 18),
                    new XlsxWriter.Column("By", "by", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("Detail", "detail", XlsxWriter.CellKind.Text, 60),
                }, J(events)),

                new("Stock ledger", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Movement", "typeName", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Location", "location", XlsxWriter.CellKind.Text, 24),
                    new XlsxWriter.Column("In", "qtyIn", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("Out", "qtyOut", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("Balance at location", "balanceAtLocation", XlsxWriter.CellKind.Integer, 12),
                    new XlsxWriter.Column("Running total", "runningTotal", XlsxWriter.CellKind.Integer, 12),
                    new XlsxWriter.Column("Rate", "rate", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Value", "value", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("By", "by", XlsxWriter.CellKind.Text, 20),
                }, J(ledger)),

                new("Purchases", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Document", "title", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Supplier", "party", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("Qty", "qty", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("Unit cost", "rate", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Amount", "amount", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Status", "status", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Detail", "detail", XlsxWriter.CellKind.Text, 50),
                }, J(events.Where(e => e.Group == "purchasing" || e.Kind == "purchase-return"))),

                new("Sales", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Document", "title", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Customer", "party", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("Qty", "qty", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("Unit price", "rate", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Amount", "amount", XlsxWriter.CellKind.Money),
                    new XlsxWriter.Column("Status", "status", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("By", "by", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("Detail", "detail", XlsxWriter.CellKind.Text, 50),
                }, J(events.Where(e => e.Kind is "sales-order" or "invoice" or "sales-return"))),

                new("Journey & delivery", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Step", "title", XlsxWriter.CellKind.Text, 36),
                    new XlsxWriter.Column("Order", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("Customer", "party", XlsxWriter.CellKind.Text, 30),
                    new XlsxWriter.Column("Qty on order", "qty", XlsxWriter.CellKind.Integer, 10),
                    new XlsxWriter.Column("Status", "status", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("By", "by", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("Detail", "detail", XlsxWriter.CellKind.Text, 50),
                }, J(events.Where(e => e.Group is "journey" or "delivery"))),

                new("Transfers", new[]
                {
                    new XlsxWriter.Column("Date", "at", XlsxWriter.CellKind.Date, 14),
                    new XlsxWriter.Column("Transfer", "reference", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("From", "from", XlsxWriter.CellKind.Text, 26),
                    new XlsxWriter.Column("To", "to", XlsxWriter.CellKind.Text, 26),
                    new XlsxWriter.Column("Qty", "qty", XlsxWriter.CellKind.Integer, 8),
                    new XlsxWriter.Column("Status", "status", XlsxWriter.CellKind.Text, 16),
                    new XlsxWriter.Column("By", "by", XlsxWriter.CellKind.Text, 20),
                    new XlsxWriter.Column("Detail", "detail", XlsxWriter.CellKind.Text, 30),
                }, J(events.Where(e => e.Group == "transfers"))),
            };

            var bytes = XlsxWriter.FromSheets(sheets);
            var safe = new string(head.Sku.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            return File(bytes, XlsxWriter.ContentType, $"{safe}-history-{Today():yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"export the history of product {productId}");
        }
    }
}
