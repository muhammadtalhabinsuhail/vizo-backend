using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Every printable document in the system except the sale invoice, which keeps
/// its own renderer and its own route because it is the one a customer sees.
///
/// Two verbs on the same URL, and the difference matters:
///
///   GET  /documents/{kind}/{id}/pdf   renders from the database and streams
///                                     the bytes. Print and Preview use this.
///   POST /documents/{kind}/{id}/pdf   renders, pushes to the "CloudinaryPdfs"
///                                     account and records the link in
///                                     "DocumentFile". Archive and Share use
///                                     this.
///
/// NOTHING IS WRITTEN TO DISK anywhere in this project. Before this controller
/// existed, none of these documents were PDFs at all -- every Print button on
/// the purchase, inventory and accounting screens called window.print(), which
/// prints the web page with its sidebar and its buttons, and stores nothing.
/// The report toolbar's "Export PDF" only produced a toast.
///
/// All of it now comes off the database and goes to the same Cloudinary
/// account as the bills.
/// </summary>
[Route("api/documents")]
[ApiController]
[Authorize(Policy = "Staff")]
public class DocumentsController : ApiControllerBase
{
    public DocumentsController(AppDbContext db, IConfiguration cfg,
        ILogger<DocumentsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /* Document kinds, and the Cloudinary sub-folder each lands in. The keys are
       what the front end sends; keeping them in one place means a typo is a
       404 with a helpful message rather than a silent empty document. */
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["purchase-order"] = "purchase-orders",
        ["purchase-invoice"] = "purchase-invoices",
        ["goods-receipt"] = "goods-receipts",
        ["purchase-return"] = "purchase-returns",
        ["stock-adjustment"] = "stock-adjustments",
        ["stock-transfer"] = "stock-transfers",
        ["voucher"] = "vouchers",
        ["journal-entry"] = "journal-entries",
        ["expense"] = "expenses",
        ["party-statement"] = "statements",
    };

    // ══════════════════════════════════════════════════════════════════
    //  RENDER  /  ARCHIVE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The document as bytes, rebuilt from the row on every request.</summary>
    [HttpGet("{kind}/{id:int}/pdf")]
    public async Task<IActionResult> Render(string kind, int id)
    {
        try
        {
            if (!Kinds.ContainsKey(kind))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var doc = await Build(kind, id);
            if (doc is null) return NotFound(new { message = $"No {kind.Replace('-', ' ')} with id {id}." });

            var name = FileName(kind, doc.DocNo, id);
            Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the {kind.Replace('-', ' ')}");
        }
    }

    /// <summary>
    /// Renders and pushes the document to the documents Cloudinary account.
    ///
    /// Re-callable. Pass force=true to rebuild one that is already archived --
    /// the company address changes, or the first upload lost the network, and
    /// the fix has to be one button.
    /// </summary>
    [HttpPost("{kind}/{id:int}/pdf")]
    public async Task<IActionResult> Archive(string kind, int id, [FromQuery] bool force = false)
    {
        try
        {
            if (!Kinds.TryGetValue(kind, out var folder))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var key = id.ToString();

            if (!force)
            {
                var existing = await DocumentArchive.FindAsync(_db, kind, key);
                if (existing is not null)
                    return Ok(Shape(existing, rebuilt: false, "That document was already archived."));
            }

            var doc = await Build(kind, id);
            if (doc is null) return NotFound(new { message = $"No {kind.Replace('-', ' ')} with id {id}." });

            var name = FileName(kind, doc.DocNo, id);
            var result = await DocumentArchive.StoreAsync(_db, _cfg, kind, key, doc.DocNo, name,
                DocumentPdf.Render(doc), CurrentUserId(), folder);

            await Log("DOCUMENT_ARCHIVED", kind, doc.DocNo ?? key, result.PdfUrl, 1);
            return Ok(Shape(result, rebuilt: true, $"{doc.DocNo ?? doc.Title} saved to the document store."));
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the {kind.Replace('-', ' ')}");
        }
    }

    /// <summary>What is already archived for a document, if anything.</summary>
    [HttpGet("{kind}/{id:int}/file")]
    public async Task<IActionResult> StoredFile(string kind, int id)
    {
        try
        {
            if (!Kinds.ContainsKey(kind))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var row = await DocumentArchive.FindAsync(_db, kind, id.ToString());
            return row is null
                ? Ok(new { archived = false })
                : Ok(Shape(row, rebuilt: false, null));
        }
        catch (Exception ex)
        {
            return Fail(ex, "look up the stored document");
        }
    }

    /// <summary>
    /// Everything the system has generated, newest first. This is the screen to
    /// open when somebody asks "where do the PDFs go" -- every row carries the
    /// Cloudinary link it was pushed to.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> List([FromQuery] string? kind, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.DocumentFiles.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(kind)) rows = rows.Where(f => f.DocKind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(f => (f.DocNo != null && f.DocNo.ToLower().Contains(term))
                                    || f.FileName.ToLower().Contains(term));
            }

            var total = await rows.CountAsync();
            var items = await rows
                .OrderByDescending(f => f.GeneratedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(f => new
                {
                    id = f.FileId,
                    kind = f.DocKind,
                    docKey = f.DocKey,
                    docNo = f.DocNo,
                    fileName = f.FileName,
                    pdfUrl = f.PdfUrl,
                    bytes = f.Bytes,
                    isDeliverable = f.IsDeliverable,
                    generatedAt = f.GeneratedAt,
                    generatedBy = _db.Users.Where(u => u.UserId == f.GeneratedByUserId)
                        .Select(u => u.FullName).FirstOrDefault()
                })
                .ToListAsync();

            return Ok(new
            {
                total, page, pageSize,
                undeliverable = await _db.DocumentFiles.CountAsync(f => !f.IsDeliverable),
                items = items.Select(i => new
                {
                    i.id, i.kind, i.docKey, i.docNo, i.fileName, i.pdfUrl, i.bytes,
                    i.isDeliverable, i.generatedAt, i.generatedBy,
                    shareUrl = ShareLink(i.kind, i.docKey)
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the document store");
        }
    }

    /// <summary>
    /// A stored document, to anybody holding the link. Anonymous on purpose:
    /// this is what goes out over WhatsApp, and the person on the other end has
    /// no account here. Same signing scheme as the sale-invoice bill link.
    /// </summary>
    [HttpGet("open/{kind}/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicDocument(string kind, string key, [FromQuery] string? k)
    {
        try
        {
            var expected = DocumentKey(kind, key);
            if (string.IsNullOrEmpty(k) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(expected)))
                return NotFound(new { message = "That link is not valid." });

            if (!Kinds.ContainsKey(kind) || !int.TryParse(key, out var id))
                return NotFound(new { message = "That link is not valid." });

            var doc = await Build(kind, id);
            if (doc is null) return NotFound(new { message = "That document no longer exists." });

            Response.Headers.ContentDisposition = $"inline; filename=\"{FileName(kind, doc.DocNo, id)}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, "open that document");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE DOCUMENTS
    // ══════════════════════════════════════════════════════════════════

    private async Task<DocumentPdf.Data?> Build(string kind, int id) => kind.ToLowerInvariant() switch
    {
        "purchase-order" => await PurchaseOrder(id),
        "purchase-invoice" => await PurchaseInvoice(id),
        "goods-receipt" => await GoodsReceipt(id),
        "purchase-return" => await PurchaseReturn(id),
        "stock-adjustment" => await StockAdjustment(id),
        "stock-transfer" => await StockTransfer(id),
        "voucher" => await Voucher(id),
        "journal-entry" => await JournalEntry(id),
        "expense" => await Expense(id),
        "party-statement" => await PartyStatement(id),
        _ => null
    };

    /* ─────────────────────────── purchases ─────────────────────────── */

    private async Task<DocumentPdf.Data?> PurchaseOrder(int id)
    {
        var o = await _db.PurchaseOrders.AsNoTracking()
            .Where(x => x.PoId == id)
            .Select(x => new
            {
                x.PoNo, x.PoDate, x.ExpectedDate, x.Subtotal, x.DiscountAmount,
                x.TaxAmount, x.TotalAmount, x.Notes,
                status = x.Status.StatusName,
                location = x.Location.LocationName,
                supplier = x.SupplierUser.LegalName,
                supplierCode = x.SupplierUser.PartyCode,
                supplierAddress = x.SupplierUser.AddressLine,
                supplierCity = x.SupplierUser.City.CityName,
                supplierPhone = x.SupplierUser.User.Phone,
                supplierNtn = x.SupplierUser.Ntn,
                createdBy = x.CreatedByUser.User.FullName,
                approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                lines = x.PurchaseOrderItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    qty = l.Quantity, cost = l.UnitCost, tax = l.TaxPercent, total = l.LineTotal
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (o is null) return null;
        var c = await LetterHead();

        return new DocumentPdf.Data(
            Company: c,
            Title: "Purchase Order",
            DocNo: o.PoNo,
            StatusName: o.status,
            Counterparty: new DocumentPdf.Party("Supplier", o.supplier, Lines(
                o.supplierCode, o.supplierAddress, o.supplierCity,
                o.supplierPhone is null ? null : $"Phone {o.supplierPhone}",
                o.supplierNtn is null ? null : $"NTN {o.supplierNtn}")),
            Meta: new[]
            {
                new DocumentPdf.Fact("PO Date", DocumentPdf.Day(o.PoDate)),
                new DocumentPdf.Fact("Expected", DocumentPdf.Day(o.ExpectedDate)),
                new DocumentPdf.Fact("Deliver To", o.location),
                new DocumentPdf.Fact("Raised By", o.createdBy),
                new DocumentPdf.Fact("Approved By", o.approvedBy ?? "Not yet"),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 5),
                new DocumentPdf.Col("Qty", 1, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Unit Cost", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Tax %", 1, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Amount", 1.7, DocumentPdf.Align.Right),
            },
            Rows: o.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name, DocumentPdf.Qty(l.qty),
                    DocumentPdf.Money(l.cost), $"{l.tax:0.##}%", DocumentPdf.Money(l.total)
                }, Sub: l.sku)).ToList(),
            Totals: Totals(c, ("Subtotal", o.Subtotal), ("Discount", -o.DiscountAmount),
                            ("Tax", o.TaxAmount), ("Order Total", o.TotalAmount)),
            Notes: o.Notes,
            Footnote: "Please quote this purchase order number on your delivery note and invoice.",
            PreparedBy: o.createdBy);
    }

    private async Task<DocumentPdf.Data?> PurchaseInvoice(int id)
    {
        var i = await _db.PurchaseInvoices.AsNoTracking()
            .Where(x => x.PiId == id)
            .Select(x => new
            {
                x.InvoiceNo, x.SupplierInvoiceNo, x.InvoiceDate, x.DueDate,
                x.Subtotal, x.DiscountAmount, x.TaxAmount, x.WhtAmount, x.TotalAmount,
                status = x.Status.StatusName,
                method = x.Method.MethodName,
                poNo = x.Po != null ? x.Po.PoNo : null,
                supplier = x.SupplierUser.LegalName,
                supplierCode = x.SupplierUser.PartyCode,
                supplierAddress = x.SupplierUser.AddressLine,
                supplierCity = x.SupplierUser.City.CityName,
                supplierNtn = x.SupplierUser.Ntn,
                createdBy = x.CreatedByUser.User.FullName,
                lines = x.PurchaseInvoiceItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    qty = l.Quantity, cost = l.UnitCost, tax = l.TaxPercent, total = l.LineTotal
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (i is null) return null;
        var c = await LetterHead();

        return new DocumentPdf.Data(
            Company: c,
            Title: "Purchase Invoice",
            DocNo: i.InvoiceNo,
            StatusName: i.status,
            Counterparty: new DocumentPdf.Party("Supplier", i.supplier, Lines(
                i.supplierCode, i.supplierAddress, i.supplierCity,
                i.supplierNtn is null ? null : $"NTN {i.supplierNtn}")),
            Meta: new[]
            {
                new DocumentPdf.Fact("Invoice Date", DocumentPdf.Day(i.InvoiceDate)),
                new DocumentPdf.Fact("Due Date", DocumentPdf.Day(i.DueDate)),
                new DocumentPdf.Fact("Their Ref", i.SupplierInvoiceNo),
                new DocumentPdf.Fact("Against PO", i.poNo ?? "-"),
                new DocumentPdf.Fact("Payment", i.method),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 5),
                new DocumentPdf.Col("Qty", 1, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Unit Cost", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Tax %", 1, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Amount", 1.7, DocumentPdf.Align.Right),
            },
            Rows: i.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name, DocumentPdf.Qty(l.qty),
                    DocumentPdf.Money(l.cost), $"{l.tax:0.##}%", DocumentPdf.Money(l.total)
                }, Sub: l.sku)).ToList(),
            Totals: Totals(c, ("Subtotal", i.Subtotal), ("Discount", -i.DiscountAmount),
                            ("Tax", i.TaxAmount), ("Withholding", -i.WhtAmount),
                            ("Invoice Total", i.TotalAmount)),
            Notes: null,
            Footnote: $"Payable by {DocumentPdf.Day(i.DueDate)}.",
            PreparedBy: i.createdBy);
    }

    private async Task<DocumentPdf.Data?> GoodsReceipt(int id)
    {
        var g = await _db.GoodsReceipts.AsNoTracking()
            .Where(x => x.GrnId == id)
            .Select(x => new
            {
                x.GrnNo, x.ReceiptDate, x.DeliveryNoteNo, x.VehicleNo, x.TotalValue, x.Notes,
                status = x.Status.StatusName,
                location = x.Location.LocationName,
                poNo = x.Po != null ? x.Po.PoNo : null,
                supplier = x.SupplierUser.LegalName,
                supplierCode = x.SupplierUser.PartyCode,
                supplierCity = x.SupplierUser.City.CityName,
                receivedBy = x.ReceivedByUser.User.FullName,
                lines = x.GoodsReceiptItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    good = l.QtyReceived, damaged = l.QtyDamaged, cost = l.UnitCost,
                    batch = l.BatchNo, expiry = l.ExpiryDate
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (g is null) return null;
        var c = await LetterHead();

        var accepted = g.lines.Sum(l => l.good);
        var rejected = g.lines.Sum(l => l.damaged);

        return new DocumentPdf.Data(
            Company: c,
            Title: "Goods Receipt Note",
            DocNo: g.GrnNo,
            StatusName: g.status,
            Counterparty: new DocumentPdf.Party("Received From", g.supplier,
                Lines(g.supplierCode, g.supplierCity)),
            Meta: new[]
            {
                new DocumentPdf.Fact("Received", DocumentPdf.Day(g.ReceiptDate)),
                new DocumentPdf.Fact("Into", g.location),
                new DocumentPdf.Fact("Against PO", g.poNo ?? "-"),
                new DocumentPdf.Fact("Delivery Note", g.DeliveryNoteNo),
                new DocumentPdf.Fact("Vehicle", string.IsNullOrWhiteSpace(g.VehicleNo) ? "-" : g.VehicleNo!),
                new DocumentPdf.Fact("Checked By", g.receivedBy),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 4.4),
                new DocumentPdf.Col("Batch", 1.4),
                new DocumentPdf.Col("Expiry", 1.4),
                new DocumentPdf.Col("Good", 0.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Damaged", 1.1, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Unit Cost", 1.4, DocumentPdf.Align.Right),
            },
            Rows: g.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name,
                    string.IsNullOrWhiteSpace(l.batch) ? "-" : l.batch!,
                    DocumentPdf.Day(l.expiry),
                    DocumentPdf.Qty(l.good),
                    l.damaged == 0 ? "-" : DocumentPdf.Qty(l.damaged),
                    DocumentPdf.Money(l.cost)
                }, Sub: l.sku)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Units accepted", DocumentPdf.Qty(accepted)),
                new DocumentPdf.Total("Units rejected", DocumentPdf.Qty(rejected),
                    Colour: rejected > 0 ? DocumentPdf.Danger : null),
                new DocumentPdf.Total("Goods Value", DocumentPdf.Money(g.TotalValue, c.CurrencySymbol), Emphasis: true),
            },
            Notes: g.Notes,
            Footnote: "Goods received in apparent good order except where a damaged quantity is shown.",
            PreparedBy: g.receivedBy);
    }

    private async Task<DocumentPdf.Data?> PurchaseReturn(int id)
    {
        var r = await _db.PurchaseReturns.AsNoTracking()
            .Where(x => x.PrId == id)
            .Select(x => new
            {
                x.ReturnNo, x.ReturnDate, x.Reason,
                status = x.Status.StatusName,
                location = x.Location.LocationName,
                invoiceNo = x.Pi.InvoiceNo,
                supplier = x.SupplierUser.LegalName,
                supplierCode = x.SupplierUser.PartyCode,
                supplierCity = x.SupplierUser.City.CityName,
                createdBy = x.CreatedByUser.User.FullName,
                lines = x.PurchaseReturnItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    qty = l.Quantity, cost = l.UnitCost
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (r is null) return null;
        var c = await LetterHead();
        var value = r.lines.Sum(l => l.qty * l.cost);

        return new DocumentPdf.Data(
            Company: c,
            Title: "Purchase Return",
            DocNo: r.ReturnNo,
            StatusName: r.status,
            Counterparty: new DocumentPdf.Party("Returned To", r.supplier,
                Lines(r.supplierCode, r.supplierCity)),
            Meta: new[]
            {
                new DocumentPdf.Fact("Return Date", DocumentPdf.Day(r.ReturnDate)),
                new DocumentPdf.Fact("Against Invoice", r.invoiceNo),
                new DocumentPdf.Fact("Out Of", r.location),
                new DocumentPdf.Fact("Raised By", r.createdBy),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 5.5),
                new DocumentPdf.Col("Qty", 1.2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Unit Cost", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Value", 1.7, DocumentPdf.Align.Right),
            },
            Rows: r.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name, DocumentPdf.Qty(l.qty),
                    DocumentPdf.Money(l.cost), DocumentPdf.Money(l.qty * l.cost)
                }, Sub: l.sku)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Units returned", DocumentPdf.Qty(r.lines.Sum(l => l.qty))),
                new DocumentPdf.Total("Credit Due", DocumentPdf.Money(value, c.CurrencySymbol), Emphasis: true),
            },
            Notes: $"Reason: {r.Reason}",
            Footnote: "Please issue a credit note against this return.",
            PreparedBy: r.createdBy);
    }

    /* ─────────────────────────── inventory ─────────────────────────── */

    private async Task<DocumentPdf.Data?> StockAdjustment(int id)
    {
        var a = await _db.StockAdjustments.AsNoTracking()
            .Where(x => x.AdjustmentId == id)
            .Select(x => new
            {
                x.AdjustmentNo, x.AdjustmentDate, x.ReasonNotes,
                status = x.Status.StatusName,
                reason = x.Reason.ReasonName,
                location = x.Location.LocationName,
                createdBy = x.CreatedByUser.User.FullName,
                lines = x.StockAdjustmentItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    was = l.CurrentQty, now = l.NewQty, cost = l.Product.CostPrice
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (a is null) return null;
        var c = await LetterHead();

        var net = a.lines.Sum(l => l.now - l.was);
        var value = a.lines.Sum(l => (l.now - l.was) * l.cost);

        return new DocumentPdf.Data(
            Company: c,
            Title: "Stock Adjustment",
            DocNo: a.AdjustmentNo,
            StatusName: a.status,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("Date", DocumentPdf.Day(a.AdjustmentDate)),
                new DocumentPdf.Fact("Location", a.location),
                new DocumentPdf.Fact("Reason", a.reason),
                new DocumentPdf.Fact("Raised By", a.createdBy),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 5),
                new DocumentPdf.Col("Counted Was", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Now", 1.2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Change", 1.2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Value", 1.6, DocumentPdf.Align.Right),
            },
            Rows: a.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name, DocumentPdf.Qty(l.was), DocumentPdf.Qty(l.now),
                    (l.now - l.was > 0 ? "+" : "") + DocumentPdf.Qty(l.now - l.was),
                    DocumentPdf.Money((l.now - l.was) * l.cost)
                }, Sub: l.sku)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Lines counted", a.lines.Count.ToString()),
                new DocumentPdf.Total("Net units", (net > 0 ? "+" : "") + DocumentPdf.Qty(net),
                    Colour: net < 0 ? DocumentPdf.Danger : DocumentPdf.Success),
                new DocumentPdf.Total("Value Change",
                    DocumentPdf.Money(value, c.CurrencySymbol), Emphasis: true),
            },
            Notes: a.ReasonNotes,
            Footnote: "Counted stock replaces the recorded quantity. The value change posts to the inventory account.",
            PreparedBy: a.createdBy);
    }

    private async Task<DocumentPdf.Data?> StockTransfer(int id)
    {
        var t = await _db.StockTransfers.AsNoTracking()
            .Where(x => x.TransferId == id)
            .Select(x => new
            {
                x.TransferNo, x.TransferDate, x.ReceivedOn, x.Notes,
                status = x.Status.StatusName,
                from = x.FromLocation.LocationName,
                to = x.ToLocation.LocationName,
                initiatedBy = x.InitiatedByUser.User.FullName,
                approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.User.FullName : null,
                lines = x.StockTransferItems.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo, name = l.Product.ProductName, sku = l.Product.Sku,
                    qty = l.Quantity, cost = l.Product.CostPrice
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (t is null) return null;
        var c = await LetterHead();

        return new DocumentPdf.Data(
            Company: c,
            Title: "Stock Transfer",
            DocNo: t.TransferNo,
            StatusName: t.status,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("Date", DocumentPdf.Day(t.TransferDate)),
                new DocumentPdf.Fact("From", t.from),
                new DocumentPdf.Fact("To", t.to),
                new DocumentPdf.Fact("Received On", DocumentPdf.Day(t.ReceivedOn)),
                new DocumentPdf.Fact("Sent By", t.initiatedBy),
                new DocumentPdf.Fact("Approved By", t.approvedBy ?? "Not yet"),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Description", 6),
                new DocumentPdf.Col("Qty", 1.4, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Value", 1.8, DocumentPdf.Align.Right),
            },
            Rows: t.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.name, DocumentPdf.Qty(l.qty),
                    DocumentPdf.Money(l.qty * l.cost)
                }, Sub: l.sku)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Lines", t.lines.Count.ToString()),
                new DocumentPdf.Total("Units", DocumentPdf.Qty(t.lines.Sum(l => l.qty))),
                new DocumentPdf.Total("Value Moved",
                    DocumentPdf.Money(t.lines.Sum(l => l.qty * l.cost), c.CurrencySymbol), Emphasis: true),
            },
            Notes: t.Notes,
            Footnote: "Sign and return one copy on receipt. Any shortfall must be reported the same day.",
            PreparedBy: t.initiatedBy);
    }

    /* ─────────────────────────── accounting ─────────────────────────── */

    private async Task<DocumentPdf.Data?> Voucher(int id)
    {
        var v = await _db.Vouchers.AsNoTracking()
            .Where(x => x.VoucherId == id)
            .Select(x => new
            {
                x.VoucherNo, x.VoucherDate, x.Amount, x.Narration, x.ReferenceNo,
                x.PaymentProvider, x.WalletTxnId,
                type = x.VoucherType.TypeName,
                /* IsReceipt, not a key string: VoucherType has TypeCode
                   (CR/CP/BR/BP/WR/WP/JV) and a boolean saying which direction
                   the money went. The boolean is the thing worth reading. */
                isReceipt = x.VoucherType.IsReceipt,
                status = x.Status.StatusName,
                method = x.Method.MethodName,
                location = x.Location.LocationName,
                party = x.PartyUser != null ? x.PartyUser.LegalName : null,
                partyCode = x.PartyUser != null ? x.PartyUser.PartyCode : null,
                partyCity = x.PartyUser != null ? x.PartyUser.City.CityName : null,
                account = x.CashBankAccount != null ? x.CashBankAccount.AccountName : null,
                createdBy = x.CreatedByUser.FullName,
                allocations = x.VoucherAllocations.Select(a => new
                {
                    salesInvoice = a.SalesInvoice != null ? a.SalesInvoice.InvoiceNo : null,
                    salesTotal = a.SalesInvoice != null ? a.SalesInvoice.TotalAmount : (decimal?)null,
                    purchaseInvoice = a.PurchaseInvoice != null ? a.PurchaseInvoice.InvoiceNo : null,
                    purchaseTotal = a.PurchaseInvoice != null ? a.PurchaseInvoice.TotalAmount : (decimal?)null,
                    a.Amount
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (v is null) return null;
        var c = await LetterHead();

        var meta = new List<DocumentPdf.Fact>
        {
            new("Date", DocumentPdf.Day(v.VoucherDate)),
            new("Type", v.type),
            new("Method", v.method),
            new("Location", v.location),
        };
        if (!string.IsNullOrWhiteSpace(v.account)) meta.Add(new("Cash / Bank", v.account!));
        if (!string.IsNullOrWhiteSpace(v.ReferenceNo)) meta.Add(new("Reference", v.ReferenceNo!));
        if (!string.IsNullOrWhiteSpace(v.WalletTxnId)) meta.Add(new("Wallet Txn", v.WalletTxnId!));

        return new DocumentPdf.Data(
            Company: c,
            Title: v.type,
            DocNo: v.VoucherNo,
            StatusName: v.status,
            Counterparty: v.party is null ? null
                : new DocumentPdf.Party(v.isReceipt ? "Received From" : "Paid To",
                    v.party, Lines(v.partyCode, v.partyCity)),
            Meta: meta,
            Columns: new[]
            {
                new DocumentPdf.Col("Applied To", 5),
                new DocumentPdf.Col("Document Total", 2.4, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Applied", 2.2, DocumentPdf.Align.Right),
            },
            Rows: v.allocations.Select(a => new DocumentPdf.Row(new[]
            {
                a.salesInvoice ?? a.purchaseInvoice ?? "On account",
                DocumentPdf.Money(a.salesTotal ?? a.purchaseTotal ?? 0m),
                DocumentPdf.Money(a.Amount)
            })).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Applied to documents",
                    DocumentPdf.Money(v.allocations.Sum(a => a.Amount), c.CurrencySymbol)),
                new DocumentPdf.Total("Unapplied",
                    DocumentPdf.Money(v.Amount - v.allocations.Sum(a => a.Amount), c.CurrencySymbol)),
                new DocumentPdf.Total("Voucher Amount",
                    DocumentPdf.Money(v.Amount, c.CurrencySymbol), Emphasis: true),
            },
            Notes: v.Narration,
            Footnote: v.isReceipt
                ? "Received with thanks, subject to realisation of the instrument where applicable."
                : "Payment made as above. Please acknowledge receipt.",
            PreparedBy: v.createdBy,
            EmptyMessage: "Nothing applied - this voucher sits on the party account.");
    }

    private async Task<DocumentPdf.Data?> JournalEntry(int id)
    {
        var e = await _db.JournalEntries.AsNoTracking()
            .Where(x => x.EntryId == id)
            .Select(x => new
            {
                x.EntryNo, x.EntryDate, x.Narration, x.ReferenceNo,
                type = x.EntryType.TypeName,
                status = x.Status.StatusName,
                location = x.Location.LocationName,
                period = x.Period.PeriodName,
                createdBy = x.CreatedByUser.FullName,
                postedBy = x.PostedByUser != null ? x.PostedByUser.FullName : null,
                lines = x.JournalEntryLines.OrderBy(l => l.LineNo).Select(l => new
                {
                    l.LineNo,
                    code = l.Account.AccountCode,
                    account = l.Account.AccountName,
                    party = l.PartyUser != null ? l.PartyUser.LegalName : null,
                    l.Description, l.DebitAmount, l.CreditAmount
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (e is null) return null;
        var c = await LetterHead();

        var debit = e.lines.Sum(l => l.DebitAmount);
        var credit = e.lines.Sum(l => l.CreditAmount);

        return new DocumentPdf.Data(
            Company: c,
            Title: "Journal Entry",
            DocNo: e.EntryNo,
            StatusName: e.status,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("Date", DocumentPdf.Day(e.EntryDate)),
                new DocumentPdf.Fact("Type", e.type),
                new DocumentPdf.Fact("Period", e.period),
                new DocumentPdf.Fact("Location", e.location),
                new DocumentPdf.Fact("Reference", string.IsNullOrWhiteSpace(e.ReferenceNo) ? "-" : e.ReferenceNo!),
                new DocumentPdf.Fact("Posted By", e.postedBy ?? "Not posted"),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("#", 0.5, DocumentPdf.Align.Centre),
                new DocumentPdf.Col("Account", 4.2),
                new DocumentPdf.Col("Party / Narration", 3),
                new DocumentPdf.Col("Debit", 1.7, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Credit", 1.7, DocumentPdf.Align.Right),
            },
            Rows: e.lines.Select(l => new DocumentPdf.Row(
                new[]
                {
                    l.LineNo.ToString(), l.account,
                    l.party ?? l.Description ?? "-",
                    l.DebitAmount == 0 ? "-" : DocumentPdf.Money(l.DebitAmount),
                    l.CreditAmount == 0 ? "-" : DocumentPdf.Money(l.CreditAmount)
                }, Sub: l.code)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Total debit", DocumentPdf.Money(debit, c.CurrencySymbol)),
                new DocumentPdf.Total("Total credit", DocumentPdf.Money(credit, c.CurrencySymbol)),
                new DocumentPdf.Total(debit == credit ? "Balanced" : "OUT OF BALANCE",
                    DocumentPdf.Money(Math.Abs(debit - credit), c.CurrencySymbol), Emphasis: true),
            },
            Notes: e.Narration,
            Footnote: null,
            PreparedBy: e.createdBy);
    }

    private async Task<DocumentPdf.Data?> Expense(int id)
    {
        var x = await _db.Expenses.AsNoTracking()
            .Where(e => e.ExpenseId == id)
            .Select(e => new
            {
                e.ExpenseNo, e.ExpenseDate, e.CategoryName, e.Amount, e.VendorName, e.Description,
                status = e.Status.StatusName,
                method = e.Method.MethodName,
                location = e.Location.LocationName,
                expenseAccount = e.ExpenseAccount.AccountName,
                expenseCode = e.ExpenseAccount.AccountCode,
                paidFrom = e.PaidFromAccount.AccountName,
                paidFromCode = e.PaidFromAccount.AccountCode,
                createdBy = e.CreatedByUser.FullName
            })
            .FirstOrDefaultAsync();

        if (x is null) return null;
        var c = await LetterHead();

        return new DocumentPdf.Data(
            Company: c,
            Title: "Expense Voucher",
            DocNo: x.ExpenseNo,
            StatusName: x.status,
            Counterparty: new DocumentPdf.Party("Paid To", x.VendorName,
                Lines(x.CategoryName, x.location)),
            Meta: new[]
            {
                new DocumentPdf.Fact("Date", DocumentPdf.Day(x.ExpenseDate)),
                new DocumentPdf.Fact("Category", x.CategoryName),
                new DocumentPdf.Fact("Method", x.method),
                new DocumentPdf.Fact("Location", x.location),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Account", 5),
                new DocumentPdf.Col("Narration", 4),
                new DocumentPdf.Col("Amount", 2, DocumentPdf.Align.Right),
            },
            Rows: new[]
            {
                new DocumentPdf.Row(
                    new[] { x.expenseAccount, x.Description ?? x.CategoryName, DocumentPdf.Money(x.Amount) },
                    Sub: $"DR {x.expenseCode}"),
                new DocumentPdf.Row(
                    new[] { x.paidFrom, $"Paid by {x.method}", $"({DocumentPdf.Money(x.Amount)})" },
                    Sub: $"CR {x.paidFromCode}"),
            },
            Totals: new[]
            {
                new DocumentPdf.Total("Expense Total",
                    DocumentPdf.Money(x.Amount, c.CurrencySymbol), Emphasis: true),
            },
            Notes: x.Description,
            Footnote: "Attach the original receipt to the office copy of this voucher.",
            PreparedBy: x.createdBy);
    }

    /* ─────────────────────────── statement ─────────────────────────── */

    /// <summary>
    /// A customer's account: every invoice, every receipt, running balance.
    /// The web version of this screen was the only document in the app already
    /// laid out for paper, and it still only offered window.print().
    /// </summary>
    private async Task<DocumentPdf.Data?> PartyStatement(int partyId)
    {
        var p = await _db.Parties.AsNoTracking()
            .Where(x => x.UserId == partyId)
            .Select(x => new
            {
                x.PartyCode, x.LegalName, x.AddressLine, x.CreditLimit, x.CreditDays, x.OpeningBalance,
                city = x.City.CityName,
                phone = x.User.Phone,
                ntn = x.Ntn
            })
            .FirstOrDefaultAsync();

        if (p is null) return null;
        var c = await LetterHead();

        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.CustomerUserId == partyId)
            .Select(i => new
            {
                date = i.InvoiceDate,
                doc = i.InvoiceNo,
                detail = "Sale invoice",
                debit = i.TotalAmount,
                credit = 0m
            })
            .ToListAsync();

        var receipts = await _db.Vouchers.AsNoTracking()
            .Where(v => v.PartyUserId == partyId && v.Status.StatusKey == "POSTED")
            .Select(v => new
            {
                date = v.VoucherDate,
                doc = v.VoucherNo,
                detail = v.VoucherType.TypeName,
                debit = 0m,
                credit = v.Amount
            })
            .ToListAsync();

        var returns = await _db.SalesReturns.AsNoTracking()
            .Where(r => r.CustomerUserId == partyId && r.Status.StatusKey != "REJECTED")
            .Select(r => new
            {
                date = r.ReturnDate,
                doc = r.ReturnNo,
                detail = "Sales return",
                debit = 0m,
                credit = r.SalesReturnItems.Sum(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m
            })
            .ToListAsync();

        var ledger = invoices.Concat(receipts).Concat(returns)
            .OrderBy(e => e.date).ThenBy(e => e.doc)
            .ToList();

        var rows = new List<DocumentPdf.Row>();
        var running = p.OpeningBalance;

        rows.Add(new DocumentPdf.Row(new[]
        {
            "-", "Opening balance", "-", "-", DocumentPdf.Money(running)
        }, Emphasis: true));

        foreach (var e in ledger)
        {
            running += e.debit - e.credit;
            rows.Add(new DocumentPdf.Row(new[]
            {
                DocumentPdf.Day(e.date),
                e.doc,
                e.debit == 0 ? "-" : DocumentPdf.Money(e.debit),
                e.credit == 0 ? "-" : DocumentPdf.Money(e.credit),
                DocumentPdf.Money(running)
            }, Sub: e.detail));
        }

        return new DocumentPdf.Data(
            Company: c,
            Title: "Account Statement",
            DocNo: p.PartyCode,
            StatusName: null,
            Counterparty: new DocumentPdf.Party("Statement For", p.LegalName, Lines(
                p.PartyCode, p.AddressLine, p.city,
                p.phone is null ? null : $"Phone {p.phone}",
                p.ntn is null ? null : $"NTN {p.ntn}")),
            Meta: new[]
            {
                new DocumentPdf.Fact("As At", DocumentPdf.Day(Today())),
                new DocumentPdf.Fact("Credit Limit",
                    p.CreditLimit > 0 ? DocumentPdf.Money(p.CreditLimit) : "No limit"),
                new DocumentPdf.Fact("Terms", p.CreditDays > 0 ? $"NET {p.CreditDays}" : "Cash"),
                new DocumentPdf.Fact("Entries", ledger.Count.ToString()),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Date", 1.8),
                new DocumentPdf.Col("Document", 3.4),
                new DocumentPdf.Col("Debit", 1.8, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Credit", 1.8, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Balance", 2, DocumentPdf.Align.Right),
            },
            Rows: rows,
            Totals: new[]
            {
                new DocumentPdf.Total("Total invoiced",
                    DocumentPdf.Money(ledger.Sum(e => e.debit), c.CurrencySymbol)),
                new DocumentPdf.Total("Total received",
                    DocumentPdf.Money(ledger.Sum(e => e.credit), c.CurrencySymbol)),
                new DocumentPdf.Total("Balance Due",
                    DocumentPdf.Money(running, c.CurrencySymbol), Emphasis: true),
            },
            Notes: null,
            Footnote: "Please settle the closing balance. Contact us within 7 days if anything here is disputed.",
            PreparedBy: null,
            EmptyMessage: "No activity on this account.");
    }

    // ══════════════════════════════════════════════════════════════════
    //  SHARED
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The company letterhead, off the single "Company" row.</summary>
    private async Task<DocumentPdf.LetterHead> LetterHead()
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

    /// <summary>Drops the empty ones, so a missing NTN is a missing line rather than "NTN ".</summary>
    private static string[] Lines(params string?[] parts) =>
        parts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToArray();

    /// <summary>
    /// A totals block from label/amount pairs, dropping the zero ones and
    /// emphasising the last. A "Discount 0.00" line on a document with no
    /// discount is noise.
    /// </summary>
    private static DocumentPdf.Total[] Totals(
        DocumentPdf.LetterHead c, params (string Label, decimal Amount)[] rows)
    {
        var kept = new List<DocumentPdf.Total>();
        for (var i = 0; i < rows.Length; i++)
        {
            var last = i == rows.Length - 1;
            if (!last && rows[i].Amount == 0) continue;
            kept.Add(new DocumentPdf.Total(rows[i].Label,
                DocumentPdf.Money(rows[i].Amount, c.CurrencySymbol), Emphasis: last));
        }
        return kept.ToArray();
    }

    private static string FileName(string kind, string? docNo, int id)
    {
        var stem = string.IsNullOrWhiteSpace(docNo) ? $"{kind}-{id}" : docNo!;
        foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '-');
        return $"{stem}.pdf";
    }

    /// <summary>
    /// Same signing scheme as the sale-invoice bill link: an HMAC of the
    /// document identity under the JWT signing secret. Unguessable, needs no
    /// column, and rotating that secret revokes every link at once.
    /// </summary>
    private string DocumentKey(string kind, string key)
    {
        var secret = _cfg["Jwt:Key"] ?? "advpos";
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = mac.ComputeHash(Encoding.UTF8.GetBytes($"doc:{kind}:{key}"));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..22];
    }

    private string ShareLink(string kind, string key) =>
        $"{Request.Scheme}://{Request.Host}/api/documents/open/{kind}/{Uri.EscapeDataString(key)}?k={DocumentKey(kind, key)}";

    private object Shape(DocumentFile f, bool rebuilt, string? message) => new
    {
        archived = true,
        fileId = f.FileId,
        kind = f.DocKind,
        docNo = f.DocNo,
        fileName = f.FileName,
        pdfUrl = f.PdfUrl,
        bytes = f.Bytes,
        isDeliverable = f.IsDeliverable,
        generatedAt = f.GeneratedAt,
        shareUrl = ShareLink(f.DocKind, f.DocKey),
        rebuilt,
        message
    };

    private object Shape(DocumentArchive.Result r, bool rebuilt, string? message) => new
    {
        archived = true,
        fileId = r.FileId,
        kind = r.DocKind,
        docNo = r.DocNo,
        fileName = r.FileName,
        pdfUrl = r.PdfUrl,
        bytes = r.Bytes,
        isDeliverable = r.Deliverable,
        generatedAt = r.GeneratedAt,
        shareUrl = ShareLink(r.DocKind, r.DocKey),
        rebuilt,
        message
    };
}
