using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Two supplier-side reports that were "Not built" cards (27 Sep, round E):
///
///   GET /reports/purchase-summary    SUPER ADMIN ONLY. Purchase orders over a
///                                    range, by supplier and by product: units,
///                                    what the supplier billed, and the price
///                                    parts on top of it (duty, Fi Sabilillah,
///                                    Margin 1, Margin 2 -- migration 26's lot
///                                    model on PurchaseOrderItem).
///   GET /reports/supplier-ledger     SUPER ADMIN and ACCOUNTANT. One supplier
///                                    over a range: balance brought forward,
///                                    bills, payments, adjustments, running
///                                    balance. The accountant sees the totals
///                                    of each bill, never its item costs.
///
/// Both also answer /pdf (GET render, POST store) and /export (.xlsx), built
/// from the same object the screen receives.
///
/// WHY PURCHASE SUMMARY IS THE SUPER ADMIN'S ALONE: the owner, 26 Sep -- "koi
/// bhi purchases ki koi bhi cheez, koi bhi item kitne mein khareeda hai ...
/// kisi bhi role ko nahi dikhni chahiye". Every figure on it is a purchase
/// price. PurchasesController is locked the same way.
///
/// WHY THE ACCOUNTANT GETS THE LEDGER: paying suppliers is his job
/// (SupplierPayablesController, 26 Sep), and a ledger of what was billed and
/// paid is what that job runs on. A bill's TOTAL says what is owed; its lines
/// say what an item cost -- so he gets the first and not the second.
/// </summary>
[Route("api/reports")]
[ApiController]
[Authorize(Policy = "Staff")]
public class PurchaseReportsController : ApiControllerBase
{
    private const int RoleSupplier = 6;
    private const int RoleBoth = 7;

    public PurchaseReportsController(AppDbContext db, IConfiguration cfg,
        ILogger<PurchaseReportsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    // ══════════════════════════════════════════════════════════════════
    //  PURCHASE SUMMARY
    // ══════════════════════════════════════════════════════════════════

    private sealed record PoLine(
        int poId, string poNo, DateOnly poDate, int supplierId, string supplier,
        int productId, string sku, string product, string category,
        int qty, decimal goods, decimal duty, decimal fs, decimal m1, decimal m2);

    [HttpGet("purchase-summary")]
    [Authorize(Roles = "super-admin")]
    public async Task<IActionResult> PurchaseSummary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? supplierId)
    {
        try { return Ok(await BuildPurchaseSummary(from, to, supplierId)); }
        catch (Exception ex) { return Fail(ex, "build the purchase summary"); }
    }

    /// <summary>
    /// Every purchase order dated in the range. Per line: goods = qty × unit
    /// cost (what the supplier charges), and each price part = qty × its unit
    /// figure. A PO's discount comes off the supplier's side only, so
    /// "supplier total" = goods − discount = the PO total the supplier billed
    /// (and Accounts Payable was credited with). "Selling value" is the whole
    /// stack -- supplier total + duty + FS + M1 + M2 -- which is what the stock
    /// went onto the shelf at.
    /// </summary>
    private async Task<object> BuildPurchaseSummary(DateOnly? from, DateOnly? to, int? supplierId)
    {
        var (start, end) = ReportKit.Range(from, to);

        var q = _db.PurchaseOrderItems.AsNoTracking()
            .Where(l => l.Po.PoDate >= start && l.Po.PoDate <= end);
        if (supplierId is not null) q = q.Where(l => l.Po.SupplierUserId == supplierId);

        var lines = await q
            .Select(l => new PoLine(
                l.PoId, l.Po.PoNo, l.Po.PoDate, l.Po.SupplierUserId,
                l.Po.SupplierUser.DisplayName ?? l.Po.SupplierUser.LegalName,
                l.ProductId, l.Product.Sku, l.Product.ProductName, l.Product.Category.CategoryName,
                l.Quantity, l.Quantity * l.UnitCost, l.Quantity * l.DutyPrice, l.Quantity * l.FsPrice,
                l.Quantity * l.Margin1Price, l.Quantity * l.Margin2Price))
            .ToListAsync();

        var poIds = lines.Select(l => l.poId).Distinct().ToList();
        var pos = await _db.PurchaseOrders.AsNoTracking()
            .Where(p => poIds.Contains(p.PoId))
            .Select(p => new
            {
                p.PoId, p.PoNo, p.PoDate, p.SupplierUserId, p.DiscountAmount, p.TotalAmount,
                billNo = p.SupplierBillNo,
                location = p.Location.LocationName
            })
            .ToDictionaryAsync(p => p.PoId);

        var bySupplier = lines.GroupBy(l => new { l.supplierId, l.supplier })
            .Select(g =>
            {
                var ids = g.Select(l => l.poId).Distinct().ToList();
                var discount = ids.Sum(id => pos[id].DiscountAmount);
                var goods = g.Sum(l => l.goods);
                var parts = g.Sum(l => l.duty + l.fs + l.m1 + l.m2);
                return new
                {
                    supplierId = g.Key.supplierId, supplier = g.Key.supplier,
                    orders = ids.Count,
                    products = g.Select(l => l.productId).Distinct().Count(),
                    units = g.Sum(l => l.qty),
                    goods, discount,
                    supplierTotal = goods - discount,
                    duty = g.Sum(l => l.duty), fs = g.Sum(l => l.fs),
                    margin1 = g.Sum(l => l.m1), margin2 = g.Sum(l => l.m2),
                    sellingValue = goods - discount + parts
                };
            })
            .OrderByDescending(s => s.supplierTotal).ToList();

        var byProduct = lines.GroupBy(l => new { l.productId, l.sku, l.product, l.category })
            .Select(g =>
            {
                var units = g.Sum(l => l.qty);
                var goods = g.Sum(l => l.goods);
                var duty = g.Sum(l => l.duty);
                var fs = g.Sum(l => l.fs);
                var m1 = g.Sum(l => l.m1);
                var m2 = g.Sum(l => l.m2);
                return new
                {
                    productId = g.Key.productId, sku = g.Key.sku, product = g.Key.product, category = g.Key.category ?? "",
                    orders = g.Select(l => l.poId).Distinct().Count(),
                    suppliers = g.Select(l => l.supplierId).Distinct().Count(),
                    units, goods,
                    averageCost = units == 0 ? 0m : Math.Round(goods / units, 2),
                    duty, fs, margin1 = m1, margin2 = m2,
                    sellingValue = goods + duty + fs + m1 + m2,
                    averageSellingPrice = units == 0 ? 0m : Math.Round((goods + duty + fs + m1 + m2) / units, 2)
                };
            })
            .OrderByDescending(p => p.goods).ToList();

        var orders = lines.GroupBy(l => l.poId)
            .Select(g =>
            {
                var p = pos[g.Key];
                return new
                {
                    id = p.PoId, poNo = p.PoNo, date = p.PoDate, supplier = g.First().supplier, p.location, p.billNo,
                    lines = g.Count(), units = g.Sum(l => l.qty),
                    supplierTotal = p.TotalAmount,
                    duty = g.Sum(l => l.duty), fs = g.Sum(l => l.fs),
                    margin1 = g.Sum(l => l.m1), margin2 = g.Sum(l => l.m2),
                    sellingValue = p.TotalAmount + g.Sum(l => l.duty + l.fs + l.m1 + l.m2)
                };
            })
            .OrderByDescending(o => o.date).ThenByDescending(o => o.id).ToList();

        return new
        {
            from = start, to = end,
            orderCount = orders.Count,
            supplierCount = bySupplier.Count,
            productCount = byProduct.Count,
            units = lines.Sum(l => l.qty),
            goods = bySupplier.Sum(s => s.goods),
            discount = bySupplier.Sum(s => s.discount),
            supplierTotal = bySupplier.Sum(s => s.supplierTotal),
            duty = bySupplier.Sum(s => s.duty),
            fs = bySupplier.Sum(s => s.fs),
            margin1 = bySupplier.Sum(s => s.margin1),
            margin2 = bySupplier.Sum(s => s.margin2),
            sellingValue = bySupplier.Sum(s => s.sellingValue),
            suppliers = await SupplierPicker(),
            bySupplier, byProduct, orders
        };
    }

    [HttpGet("purchase-summary/pdf")]
    [Authorize(Roles = "super-admin")]
    public async Task<IActionResult> PurchaseSummaryPdf([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? supplierId)
    {
        try
        {
            var (doc, file, _) = await PurchaseSummaryDoc(from, to, supplierId);
            Response.Headers.ContentDisposition = $"inline; filename=\"{file}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex) { return Fail(ex, "render the purchase summary"); }
    }

    [HttpPost("purchase-summary/pdf")]
    [Authorize(Roles = "super-admin")]
    public async Task<IActionResult> PurchaseSummaryArchive([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? supplierId)
    {
        try
        {
            var (doc, file, key) = await PurchaseSummaryDoc(from, to, supplierId);
            return await Archive("report.purchase-summary", key, doc, file);
        }
        catch (Exception ex) { return Fail(ex, "archive the purchase summary"); }
    }

    private async Task<(DocumentPdf.Data, string, string)> PurchaseSummaryDoc(DateOnly? from, DateOnly? to, int? supplierId)
    {
        var j = ReportKit.ToJson(await BuildPurchaseSummary(from, to, supplierId));
        var c = await ReportKit.LetterHeadAsync(_db);
        var cur = c.CurrencySymbol;
        var start = DateOnly.Parse(S(j, "from"));
        var end = DateOnly.Parse(S(j, "to"));

        var doc = new DocumentPdf.Data(
            Company: c,
            Title: "Purchase Summary",
            DocNo: null, StatusName: null, Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", DocumentPdf.Day(start)),
                new DocumentPdf.Fact("To", DocumentPdf.Day(end)),
                new DocumentPdf.Fact("Orders", D(j, "orderCount").ToString("N0")),
                new DocumentPdf.Fact("Units", D(j, "units").ToString("N0")),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Supplier", 3.0),
                new DocumentPdf.Col("POs", 0.8, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Units", 1.0, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Supplier Total", 1.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Duty", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("FS", 1.4, DocumentPdf.Align.Right),
                new DocumentPdf.Col("M1 + M2", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Selling Value", 1.9, DocumentPdf.Align.Right),
            },
            Rows: j.GetProperty("bySupplier").EnumerateArray().Select(r => new DocumentPdf.Row(new[]
            {
                S(r, "supplier"), D(r, "orders").ToString("N0"), D(r, "units").ToString("N0"),
                DocumentPdf.Money(D(r, "supplierTotal")), ReportKit.Zero(D(r, "duty")), ReportKit.Zero(D(r, "fs")),
                ReportKit.Zero(D(r, "margin1") + D(r, "margin2")), DocumentPdf.Money(D(r, "sellingValue"))
            }, Sub: D(r, "discount") > 0 ? $"discount {DocumentPdf.Money(D(r, "discount"))}" : null)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Goods at cost", DocumentPdf.Money(D(j, "goods"), cur)),
                new DocumentPdf.Total("Discount", DocumentPdf.Money(-D(j, "discount"), cur), Colour: DocumentPdf.Danger),
                new DocumentPdf.Total("Supplier total", DocumentPdf.Money(D(j, "supplierTotal"), cur)),
                new DocumentPdf.Total("Duty", DocumentPdf.Money(D(j, "duty"), cur)),
                new DocumentPdf.Total("Fi Sabilillah", DocumentPdf.Money(D(j, "fs"), cur)),
                new DocumentPdf.Total("Margin 1", DocumentPdf.Money(D(j, "margin1"), cur)),
                new DocumentPdf.Total("Margin 2", DocumentPdf.Money(D(j, "margin2"), cur)),
                new DocumentPdf.Total("Selling Value", DocumentPdf.Money(D(j, "sellingValue"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Supplier total = quantity × unit cost less the PO discount -- what the supplier billed. " +
                      "Selling value = supplier total + duty + Fi Sabilillah + Margin 1 + Margin 2.",
            PreparedBy: null,
            EmptyMessage: "No purchase orders in this range.",
            More: new[]
            {
                new DocumentPdf.Section("By product",
                    new[]
                    {
                        new DocumentPdf.Col("Item", 3.4),
                        new DocumentPdf.Col("Units", 1.0, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Avg Cost", 1.5, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Goods", 1.8, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Duty", 1.5, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("FS + M1 + M2", 1.8, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Selling Value", 1.9, DocumentPdf.Align.Right),
                    },
                    j.GetProperty("byProduct").EnumerateArray().Select(r => new DocumentPdf.Row(new[]
                    {
                        S(r, "product"), D(r, "units").ToString("N0"), DocumentPdf.Money(D(r, "averageCost")),
                        DocumentPdf.Money(D(r, "goods")), ReportKit.Zero(D(r, "duty")),
                        ReportKit.Zero(D(r, "fs") + D(r, "margin1") + D(r, "margin2")),
                        DocumentPdf.Money(D(r, "sellingValue"))
                    }, Sub: $"{S(r, "sku")}  ·  {S(r, "category")}")).ToList())
            });
        return (doc, $"purchase-summary-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.pdf",
            $"{start:yyyy-MM-dd}:{end:yyyy-MM-dd}:{supplierId?.ToString() ?? "all"}");
    }

    [HttpGet("purchase-summary/export")]
    [Authorize(Roles = "super-admin")]
    public async Task<IActionResult> PurchaseSummaryExport([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? supplierId)
    {
        try
        {
            var j = ReportKit.ToJson(await BuildPurchaseSummary(from, to, supplierId));
            var parts = new[]
            {
                new XlsxWriter.Column("Duty", "duty", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Fi Sabilillah", "fs", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Margin 1", "margin1", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Margin 2", "margin2", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Selling Value", "sellingValue", XlsxWriter.CellKind.Money),
            };
            var bytes = XlsxWriter.FromSheets(new[]
            {
                new XlsxWriter.SheetSpec("By supplier", new XlsxWriter.Column[]
                {
                    new("Supplier", "supplier", XlsxWriter.CellKind.Text, 32),
                    new("POs", "orders", XlsxWriter.CellKind.Integer),
                    new("Items", "products", XlsxWriter.CellKind.Integer),
                    new("Units", "units", XlsxWriter.CellKind.Integer),
                    new("Goods at Cost", "goods", XlsxWriter.CellKind.Money),
                    new("Discount", "discount", XlsxWriter.CellKind.Money),
                    new("Supplier Total", "supplierTotal", XlsxWriter.CellKind.Money),
                }.Concat(parts).ToList(), j.GetProperty("bySupplier")),
                new XlsxWriter.SheetSpec("By product", new XlsxWriter.Column[]
                {
                    new("SKU", "sku", XlsxWriter.CellKind.Text, 16),
                    new("Item", "product", XlsxWriter.CellKind.Text, 36),
                    new("Category", "category", XlsxWriter.CellKind.Text, 18),
                    new("POs", "orders", XlsxWriter.CellKind.Integer),
                    new("Suppliers", "suppliers", XlsxWriter.CellKind.Integer),
                    new("Units", "units", XlsxWriter.CellKind.Integer),
                    new("Average Cost", "averageCost", XlsxWriter.CellKind.Money),
                    new("Goods at Cost", "goods", XlsxWriter.CellKind.Money),
                }.Concat(parts).Append(new("Average Selling Price", "averageSellingPrice", XlsxWriter.CellKind.Money)).ToList(),
                    j.GetProperty("byProduct")),
                new XlsxWriter.SheetSpec("Purchase orders", new XlsxWriter.Column[]
                {
                    new("PO", "poNo", XlsxWriter.CellKind.Text, 16),
                    new("Date", "date", XlsxWriter.CellKind.Date),
                    new("Supplier", "supplier", XlsxWriter.CellKind.Text, 30),
                    new("Supplier Bill", "billNo", XlsxWriter.CellKind.Text, 16),
                    new("Received At", "location", XlsxWriter.CellKind.Text, 20),
                    new("Lines", "lines", XlsxWriter.CellKind.Integer),
                    new("Units", "units", XlsxWriter.CellKind.Integer),
                    new("Supplier Total", "supplierTotal", XlsxWriter.CellKind.Money),
                }.Concat(parts).ToList(), j.GetProperty("orders")),
            });
            return File(bytes, XlsxWriter.ContentType, $"purchase-summary-{S(j, "from")}-to-{S(j, "to")}.xlsx");
        }
        catch (Exception ex) { return Fail(ex, "export the purchase summary"); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SUPPLIER LEDGER
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Suppliers for the picker, with what is owed to each today (payable sense: + = we owe).</summary>
    [HttpGet("supplier-ledger/suppliers")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> LedgerSuppliers()
    {
        try
        {
            var list = await SupplierPicker();
            var today = Today();
            var result = new List<object>();
            foreach (var s in list)
            {
                var events = await Events(s.id, includeItems: false);
                var opening = -s.opening;
                result.Add(new
                {
                    s.id, s.code, s.name, s.city,
                    balance = opening + events.Where(e => e.date <= today).Sum(e => e.credit - e.debit),
                    lastActivity = events.Count == 0 ? (DateOnly?)null : events.Max(e => e.date)
                });
            }
            return Ok(result);
        }
        catch (Exception ex) { return Fail(ex, "load the suppliers"); }
    }

    private sealed record SupplierRow(int id, string code, string name, string? city, decimal opening);

    private async Task<List<SupplierRow>> SupplierPicker() =>
        await _db.Parties.AsNoTracking()
            .Where(p => p.User.RoleId == RoleSupplier || p.User.RoleId == RoleBoth)
            .OrderBy(p => p.DisplayName ?? p.LegalName)
            .Select(p => new SupplierRow(p.UserId, p.PartyCode, p.DisplayName ?? p.LegalName, p.City.CityName, p.OpeningBalance))
            .ToListAsync();

    public sealed record LedgerItem(string name, int qty, decimal unitCost, decimal amount);

    public sealed record LedgerEvent(
        DateOnly date, string kind, string reference, string particulars, string? detail,
        decimal debit, decimal credit, int? documentId, List<LedgerItem>? items);

    /// <summary>
    /// Everything that ever moved what we owe this supplier, oldest first, in
    /// the PAYABLE sense (credit = we owe more, debit = we owe less):
    ///
    ///   bill        each purchase invoice (not void) -- credit its total. Since
    ///               26 Sep a purchase order raises one the moment it is saved;
    ///               the older seeded ones carry no journal entry at all, which
    ///               is why this reads the DOCUMENTS rather than only 2101 (a
    ///               books-only ledger would lose six of the eight bills);
    ///   payment     each POSTED payment voucher to the supplier -- debit;
    ///               a receipt voucher from him (a refund) -- credit;
    ///   adjustment  any other posted line on 2101 Accounts Payable carrying
    ///               the supplier (PartyUserId), i.e. not a bill's or a
    ///               voucher's own entry: a hand-written journal.
    ///
    /// That is how the customer ledger (session B) reads 1130 by party; here
    /// the documents lead because the supplier side was never fully posted.
    /// </summary>
    private async Task<List<LedgerEvent>> Events(int supplierId, bool includeItems)
    {
        var bills = await _db.PurchaseInvoices.AsNoTracking()
            .Where(i => i.SupplierUserId == supplierId && i.Status.StatusKey != "VOID")
            .Select(i => new
            {
                i.PiId, i.InvoiceNo, i.SupplierInvoiceNo, i.InvoiceDate, i.DueDate, i.TotalAmount, i.EntryId,
                poNo = i.Po != null ? i.Po.PoNo : null,
                items = i.PurchaseInvoiceItems.OrderBy(x => x.LineNo)
                    .Select(x => new LedgerItem(x.Product.ProductName, x.Quantity, x.UnitCost, x.LineTotal)).ToList()
            })
            .ToListAsync();

        var vouchers = await _db.Vouchers.AsNoTracking()
            .Where(v => v.PartyUserId == supplierId && v.Status.StatusKey == "POSTED")
            .Select(v => new
            {
                v.VoucherId, v.VoucherNo, v.VoucherDate, v.Amount, v.EntryId, v.ReferenceNo,
                receipt = v.VoucherType.IsReceipt,
                type = v.VoucherType.TypeName,
                account = v.CashBankAccount != null ? v.CashBankAccount.AccountName : null,
                method = v.Method.MethodName,
                bills = v.VoucherAllocations.Where(a => a.PurchaseInvoice != null)
                    .Select(a => a.PurchaseInvoice!.InvoiceNo).ToList()
            })
            .ToListAsync();

        var ap = await LedgerPosting.AccountIdAsync(_db, "2101") ?? 0;
        var docEntries = bills.Where(b => b.EntryId != null).Select(b => b.EntryId!.Value)
            .Concat(vouchers.Where(v => v.EntryId != null).Select(v => v.EntryId!.Value)).ToList();
        var adjustments = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == ap && l.PartyUserId == supplierId &&
                        l.Entry.Status.StatusKey == "POSTED" && !docEntries.Contains(l.EntryId))
            .Select(l => new
            {
                l.EntryId, entryNo = l.Entry.EntryNo, date = l.Entry.EntryDate, l.Description,
                narration = l.Entry.Narration, l.DebitAmount, l.CreditAmount
            })
            .ToListAsync();

        var events = new List<LedgerEvent>();
        events.AddRange(bills.Select(b => new LedgerEvent(
            b.InvoiceDate, "bill", b.InvoiceNo,
            $"Bill {b.SupplierInvoiceNo}" + (b.poNo is null ? "" : $" · {b.poNo}"),
            $"due {b.DueDate:dd MMM yyyy}", 0m, b.TotalAmount, b.PiId,
            includeItems ? b.items : null)));
        events.AddRange(vouchers.Select(v => new LedgerEvent(
            v.VoucherDate, v.receipt ? "refund" : "payment", v.VoucherNo,
            $"{v.type} · {v.account ?? v.method}" + (string.IsNullOrWhiteSpace(v.ReferenceNo) ? "" : $" · ref {v.ReferenceNo}"),
            v.bills.Count == 0 ? "on account" : $"pays {string.Join(", ", v.bills)}",
            v.receipt ? 0m : v.Amount, v.receipt ? v.Amount : 0m, v.VoucherId, null)));
        events.AddRange(adjustments.Select(a => new LedgerEvent(
            a.date, "adjustment", a.entryNo,
            string.IsNullOrWhiteSpace(a.Description) ? a.narration : a.Description!,
            null, a.DebitAmount, a.CreditAmount, a.EntryId, null)));

        return events.OrderBy(e => e.date).ThenBy(e => e.kind == "bill" ? 0 : 1).ThenBy(e => e.reference).ToList();
    }

    public sealed record LedgerRow(
        DateOnly date, string kind, string reference, string particulars, string? detail,
        decimal debit, decimal credit, decimal balance, int? documentId, List<LedgerItem>? items);

    [HttpGet("supplier-ledger")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SupplierLedger([FromQuery] int? supplierId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            if (supplierId is null) return BadRequest(new { message = "Pick a supplier." });
            var l = await BuildLedger(supplierId.Value, from, to);
            return l is null ? NotFound(new { message = $"No supplier with id {supplierId}." }) : Ok(l);
        }
        catch (Exception ex) { return Fail(ex, $"build the ledger for supplier {supplierId}"); }
    }

    private async Task<object?> BuildLedger(int supplierId, DateOnly? from, DateOnly? to)
    {
        var s = await _db.Parties.AsNoTracking()
            .Where(p => p.UserId == supplierId && (p.User.RoleId == RoleSupplier || p.User.RoleId == RoleBoth))
            .Select(p => new
            {
                id = p.UserId, code = p.PartyCode, name = p.DisplayName ?? p.LegalName,
                city = p.City.CityName, phone = p.User.Phone, p.CreditDays, p.OpeningBalance
            })
            .FirstOrDefaultAsync();
        if (s is null) return null;

        /* Default window: the start of the year to today -- a supplier is paid
           in weeks, not days, and one month rarely shows a bill AND its payment. */
        var today = Today();
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, 1, 1);
        if (start > end) (start, end) = (end, start);

        var seesItems = CurrentRole() == OrderWorkflow.RoleAdmin;
        var events = await Events(supplierId, seesItems);

        /* Party.OpeningBalance is in the debit sense (negative = we owe him);
           this ledger reads in the payable sense, so it is flipped once, here. */
        var opening = -s.OpeningBalance;
        var bf = opening + events.Where(e => e.date < start).Sum(e => e.credit - e.debit);

        var running = bf;
        var rows = new List<LedgerRow>();
        foreach (var e in events.Where(e => e.date >= start && e.date <= end))
        {
            running += e.credit - e.debit;
            rows.Add(new LedgerRow(e.date, e.kind, e.reference, e.particulars, e.detail,
                e.debit, e.credit, running, e.documentId, e.items));
        }

        return new
        {
            supplier = new { s.id, s.code, s.name, s.city, s.phone, creditDays = s.CreditDays, openingBalance = opening },
            from = start, to = end,
            showsItems = seesItems,
            balanceBroughtForward = bf,
            bills = rows.Where(r => r.kind is "bill").Sum(r => r.credit),
            payments = rows.Where(r => r.kind is "payment").Sum(r => r.debit),
            refunds = rows.Where(r => r.kind is "refund").Sum(r => r.credit),
            adjustments = rows.Where(r => r.kind is "adjustment").Sum(r => r.credit - r.debit),
            totalDebit = rows.Sum(r => r.debit),
            totalCredit = rows.Sum(r => r.credit),
            closingBalance = running,
            count = rows.Count,
            rows
        };
    }

    [HttpGet("supplier-ledger/pdf")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SupplierLedgerPdf([FromQuery] int? supplierId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            if (supplierId is null) return BadRequest(new { message = "Pick a supplier." });
            var built = await LedgerDoc(supplierId.Value, from, to);
            if (built is null) return NotFound(new { message = $"No supplier with id {supplierId}." });
            Response.Headers.ContentDisposition = $"inline; filename=\"{built.Value.File}\"";
            return File(DocumentPdf.Render(built.Value.Doc), "application/pdf");
        }
        catch (Exception ex) { return Fail(ex, "render the supplier ledger"); }
    }

    [HttpPost("supplier-ledger/pdf")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SupplierLedgerArchive([FromQuery] int? supplierId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            if (supplierId is null) return BadRequest(new { message = "Pick a supplier." });
            var built = await LedgerDoc(supplierId.Value, from, to);
            if (built is null) return NotFound(new { message = $"No supplier with id {supplierId}." });
            return await Archive("report.supplier-ledger", built.Value.Key, built.Value.Doc, built.Value.File);
        }
        catch (Exception ex) { return Fail(ex, "archive the supplier ledger"); }
    }

    private async Task<(DocumentPdf.Data Doc, string File, string Key)?> LedgerDoc(int supplierId, DateOnly? from, DateOnly? to)
    {
        var built = await BuildLedger(supplierId, from, to);
        if (built is null) return null;
        var j = ReportKit.ToJson(built);
        var sup = j.GetProperty("supplier");
        var c = await ReportKit.LetterHeadAsync(_db);
        var cur = c.CurrencySymbol;
        var start = DateOnly.Parse(S(j, "from"));
        var end = DateOnly.Parse(S(j, "to"));

        var rows = new List<DocumentPdf.Row>
        {
            new(new[] { DocumentPdf.Day(start), "", "Balance brought forward", "", "", DocumentPdf.Money(D(j, "balanceBroughtForward")) }, Emphasis: true)
        };
        /* A bill's lines (Super Admin only -- the accountant's answer carries
           none) get a table of their own under the ledger: the renderer puts a
           row's sub-line under its first, narrow column, where a list of items
           would be cut off after a few words. */
        var itemRows = new List<DocumentPdf.Row>();
        foreach (var r in j.GetProperty("rows").EnumerateArray())
        {
            var sub = S(r, "detail");
            if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                itemRows.AddRange(items.EnumerateArray().Select(x => new DocumentPdf.Row(new[]
                {
                    S(r, "reference"), S(x, "name"), D(x, "qty").ToString("N0"),
                    DocumentPdf.Money(D(x, "unitCost")), DocumentPdf.Money(D(x, "amount"))
                })));
            rows.Add(new DocumentPdf.Row(new[]
            {
                DocumentPdf.Day(DateOnly.Parse(S(r, "date"))), S(r, "reference"), S(r, "particulars"),
                ReportKit.Zero(D(r, "debit")), ReportKit.Zero(D(r, "credit")), DocumentPdf.Money(D(r, "balance"))
            }, Sub: string.IsNullOrWhiteSpace(sub) ? null : sub));
        }

        var doc = new DocumentPdf.Data(
            Company: c,
            Title: "Supplier Ledger",
            DocNo: S(sup, "code"),
            StatusName: null,
            Counterparty: new DocumentPdf.Party("Supplier", S(sup, "name"),
                new[] { S(sup, "city"), S(sup, "phone") }.Where(x => !string.IsNullOrWhiteSpace(x)).ToList()),
            Meta: new[]
            {
                new DocumentPdf.Fact("From", DocumentPdf.Day(start)),
                new DocumentPdf.Fact("To", DocumentPdf.Day(end)),
                new DocumentPdf.Fact("Entries", D(j, "count").ToString("N0")),
                new DocumentPdf.Fact("Closing", DocumentPdf.Money(D(j, "closingBalance"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Date", 1.6),
                new DocumentPdf.Col("Reference", 1.7),
                new DocumentPdf.Col("Particulars", 3.6),
                new DocumentPdf.Col("Paid (Dr)", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Billed (Cr)", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Balance", 1.8, DocumentPdf.Align.Right),
            },
            Rows: rows,
            Totals: new[]
            {
                new DocumentPdf.Total("Brought forward", DocumentPdf.Money(D(j, "balanceBroughtForward"), cur)),
                new DocumentPdf.Total("Bills", DocumentPdf.Money(D(j, "bills"), cur)),
                new DocumentPdf.Total("Payments", DocumentPdf.Money(-D(j, "payments"), cur), Colour: DocumentPdf.Success),
                new DocumentPdf.Total("Adjustments", DocumentPdf.Money(D(j, "adjustments") + D(j, "refunds"), cur)),
                new DocumentPdf.Total("We owe", DocumentPdf.Money(D(j, "closingBalance"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Balance in the payable sense: a positive figure is what we owe the supplier. " +
                      "Bills are purchase invoices; payments are posted payment vouchers.",
            PreparedBy: null,
            EmptyMessage: null,
            More: itemRows.Count == 0 ? null : new[]
            {
                new DocumentPdf.Section("Bill items", new[]
                {
                    new DocumentPdf.Col("Bill", 1.6),
                    new DocumentPdf.Col("Item", 4.2),
                    new DocumentPdf.Col("Qty", 1.0, DocumentPdf.Align.Right),
                    new DocumentPdf.Col("Unit Cost", 1.6, DocumentPdf.Align.Right),
                    new DocumentPdf.Col("Amount", 1.8, DocumentPdf.Align.Right),
                }, itemRows)
            });

        return (doc, $"supplier-ledger-{S(sup, "code")}-{start:yyyy-MM-dd}-to-{end:yyyy-MM-dd}.pdf",
            $"{supplierId}:{start:yyyy-MM-dd}:{end:yyyy-MM-dd}:{(j.GetProperty("showsItems").GetBoolean() ? "i" : "t")}");
    }

    [HttpGet("supplier-ledger/export")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SupplierLedgerExport([FromQuery] int? supplierId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            if (supplierId is null) return BadRequest(new { message = "Pick a supplier." });
            var built = await BuildLedger(supplierId.Value, from, to);
            if (built is null) return NotFound(new { message = $"No supplier with id {supplierId}." });
            var j = ReportKit.ToJson(built);

            /* The B/F line is a row of the sheet too, so the running balance in
               the file starts from the same figure the screen does. */
            var bfRow = new[]
            {
                new
                {
                    date = S(j, "from"), kind = "opening", reference = "", particulars = "Balance brought forward",
                    detail = "", debit = 0m, credit = 0m, balance = D(j, "balanceBroughtForward")
                }
            };
            var rows = bfRow.Concat(j.GetProperty("rows").EnumerateArray().Select(r => new
            {
                date = S(r, "date"), kind = S(r, "kind"), reference = S(r, "reference"),
                particulars = S(r, "particulars"), detail = S(r, "detail"),
                debit = D(r, "debit"), credit = D(r, "credit"), balance = D(r, "balance")
            })).ToList();

            var sheets = new List<XlsxWriter.SheetSpec>
            {
                new("Ledger", new XlsxWriter.Column[]
                {
                    new("Date", "date", XlsxWriter.CellKind.Date),
                    new("Type", "kind", XlsxWriter.CellKind.Text, 12),
                    new("Reference", "reference", XlsxWriter.CellKind.Text, 16),
                    new("Particulars", "particulars", XlsxWriter.CellKind.Text, 40),
                    new("Detail", "detail", XlsxWriter.CellKind.Text, 26),
                    new("Paid (Dr)", "debit", XlsxWriter.CellKind.Money),
                    new("Billed (Cr)", "credit", XlsxWriter.CellKind.Money),
                    new("Balance", "balance", XlsxWriter.CellKind.Money),
                }, ReportKit.ToJson(rows))
            };

            /* Item costs: the Super Admin's sheet only (the accountant's answer
               has no items in it to begin with). */
            if (j.GetProperty("showsItems").GetBoolean())
            {
                var items = j.GetProperty("rows").EnumerateArray()
                    .Where(r => r.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array)
                    .SelectMany(r => r.GetProperty("items").EnumerateArray().Select(x => new
                    {
                        bill = S(r, "reference"), date = S(r, "date"), item = S(x, "name"),
                        qty = D(x, "qty"), unitCost = D(x, "unitCost"), amount = D(x, "amount")
                    })).ToList();
                sheets.Add(new("Bill items", new XlsxWriter.Column[]
                {
                    new("Bill", "bill", XlsxWriter.CellKind.Text, 16),
                    new("Date", "date", XlsxWriter.CellKind.Date),
                    new("Item", "item", XlsxWriter.CellKind.Text, 36),
                    new("Qty", "qty", XlsxWriter.CellKind.Integer),
                    new("Unit Cost", "unitCost", XlsxWriter.CellKind.Money),
                    new("Amount", "amount", XlsxWriter.CellKind.Money),
                }, ReportKit.ToJson(items)));
            }

            var sup = j.GetProperty("supplier");
            return File(XlsxWriter.FromSheets(sheets), XlsxWriter.ContentType,
                $"supplier-ledger-{S(sup, "code")}-{S(j, "from")}-to-{S(j, "to")}.xlsx");
        }
        catch (Exception ex) { return Fail(ex, "export the supplier ledger"); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  shared
    // ══════════════════════════════════════════════════════════════════

    private static decimal D(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static string S(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private async Task<IActionResult> Archive(string kind, string key, DocumentPdf.Data doc, string file)
    {
        var stored = await DocumentArchive.StoreAsync(_db, _cfg, kind, key, doc.Title, file,
            DocumentPdf.Render(doc), CurrentUserId(), "reports");
        await Log("REPORT_ARCHIVED", kind, doc.Title, stored.PdfUrl, 1);
        return Ok(new
        {
            archived = true, fileId = stored.FileId, kind, fileName = stored.FileName,
            pdfUrl = stored.PdfUrl, bytes = stored.Bytes, isDeliverable = stored.Deliverable,
            generatedAt = stored.GeneratedAt,
            message = stored.Deliverable
                ? $"{doc.Title} saved to the document store."
                : $"{doc.Title} saved. The store will not serve PDFs yet -- see the Cloudinary setting."
        });
    }
}
