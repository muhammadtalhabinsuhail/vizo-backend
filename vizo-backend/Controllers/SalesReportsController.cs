using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Reports that were a "Not built" card, and two report buttons that were a
/// toast (27 Sep, round E):
///
///   GET  /reports/sales-by-rep            per salesperson over a range
///   GET  /reports/sales-by-product        per product and category over a range
///   POST /reports/aging/customer/reminders   "Send Reminders" on AR Aging
///   POST /reports/aging/customer/reminders/whatsapp   records a WhatsApp reminder opened
///   POST /reports/dead-stock/clearance    "Plan Clearance" on Dead Stock -- an .xlsx
///
/// Each report also answers /pdf (GET renders, POST stores it in the document
/// store -- the same two calls the shared report toolbar makes for every other
/// report) and /export (.xlsx). The PDF and the sheet are built from the SAME
/// object the screen receives, so paper, spreadsheet and screen cannot disagree
/// -- the rule ReportsController follows for its six.
///
/// Literal routes ("sales-by-rep/pdf") win over ReportsController's
/// "{key}/pdf" template, so the two controllers share the /reports prefix
/// without colliding.
///
/// WHO SEES WHAT, by role (a permission ticked in Setup opens a screen, it does
/// not widen what is in it):
///   · the order desk: none of it -- it is all money (the owner, 26 Sep);
///   · a sales rep: his own row on Sales by Salesperson, and Sales by Product
///     over his own invoices only;
///   · COST AND PROFIT: the Super Admin alone. The owner's rule is that what an
///     item cost is his; for everybody else the cost fields are not blank or
///     zero, they are not in the answer at all.
/// </summary>
[Route("api/reports")]
[ApiController]
[Authorize(Policy = "Staff")]
public class SalesReportsController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    /* Invoices that are not real sales yet (a draft) or no longer (void). */
    private static readonly string[] NotASale = { "DRAFT", "VOID" };
    /* Orders that were never taken: a draft, or refused, or cancelled. */
    private static readonly string[] NotAnOrder = { "DRAFT", "DECLINED", "CANCELLED" };
    /* Returns that moved money: approved or posted (SalesController posts both). */
    private static readonly string[] CountedReturns = { "APPROVED", "POSTED" };

    public SalesReportsController(AppDbContext db, IConfiguration cfg,
        ILogger<SalesReportsController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    private int? RepScope() => CurrentRole() == OrderWorkflow.RoleSales ? CurrentUserId() : null;
    private bool SeesCost() => CurrentRole() == OrderWorkflow.RoleAdmin;

    // ══════════════════════════════════════════════════════════════════
    //  SALES BY SALESPERSON
    // ══════════════════════════════════════════════════════════════════

    public sealed record RepRow(
        int? repId, string repName, string? repRole, int customers, int orders, decimal orderValue,
        int invoices, decimal invoiced, decimal returns, decimal netSales,
        decimal collected, decimal outstanding, int visits);

    public sealed record SalesByRep(
        DateOnly from, DateOnly to, bool scopedToRep, int count,
        RepRow totals, List<RepRow> items);

    [HttpGet("sales-by-rep")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByRepReport([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try { return Ok(await BuildSalesByRep(from, to)); }
        catch (Exception ex) { return Fail(ex, "build sales by salesperson"); }
    }

    /// <summary>
    /// One row per salesperson. What each column counts:
    ///   orders       orders taken in the range (not drafts, declined or cancelled), and their value;
    ///   invoiced     invoices dated in the range on that rep's orders;
    ///   returns      approved/posted returns dated in the range against those invoices
    ///                (or, for a return with no invoice, against his customer);
    ///   net sales    invoiced - returns;
    ///   collected    confirmed collections received in the range on his orders;
    ///   outstanding  what his assigned customers owe as at the end of the range,
    ///                off the books (opening balance + 1130 lines) -- the same
    ///                figure the customer ledger shows;
    ///   visits       customer visits he logged in the range.
    /// Counter sales and direct invoices have no salesperson; they, and
    /// customers nobody is assigned to, sit on a "No salesperson" row the back
    /// office sees, so the column totals still add up to the business.
    /// </summary>
    private async Task<SalesByRep> BuildSalesByRep(DateOnly? from, DateOnly? to)
    {
        var (start, end) = ReportKit.Range(from, to);
        var me = RepScope();
        var startAt = start.ToDateTime(TimeOnly.MinValue);
        var endAt = end.AddDays(1).ToDateTime(TimeOnly.MinValue);

        var orders = await _db.SalesOrders.AsNoTracking()
            .Where(o => o.OrderDate >= start && o.OrderDate <= end && !NotAnOrder.Contains(o.Status.StatusKey))
            .GroupBy(o => o.SalesPersonUserId)
            .Select(g => new { rep = g.Key, n = g.Count(), value = g.Sum(o => o.TotalAmount) })
            .ToListAsync();

        var invoices = await _db.SalesInvoices.AsNoTracking()
            .Where(i => i.InvoiceDate >= start && i.InvoiceDate <= end && !NotASale.Contains(i.Status.StatusKey))
            .GroupBy(i => i.Order != null ? i.Order.SalesPersonUserId : null)
            .Select(g => new { rep = g.Key, n = g.Count(), value = g.Sum(i => i.TotalAmount) })
            .ToListAsync();

        var returns = await _db.SalesReturns.AsNoTracking()
            .Where(r => r.ReturnDate >= start && r.ReturnDate <= end && CountedReturns.Contains(r.Status.StatusKey))
            .Select(r => new
            {
                rep = r.Invoice != null && r.Invoice.Order != null
                    ? r.Invoice.Order.SalesPersonUserId
                    : r.CustomerUser.SalesPersonUserId,
                value = r.SalesReturnItems.Sum(x => (decimal?)(x.Quantity * x.UnitPrice)) ?? 0m
            })
            .ToListAsync();

        var collected = await _db.CollectionAllocations.AsNoTracking()
            .Where(a => a.Collection.Status.StatusKey == "CONFIRMED" &&
                        a.Collection.CollectedOn >= start && a.Collection.CollectedOn <= end)
            .GroupBy(a => a.Order.SalesPersonUserId)
            .Select(g => new { rep = g.Key, value = g.Sum(a => a.Amount) })
            .ToListAsync();

        /* Outstanding, off the books: every customer's opening balance plus his
           posted 1130 lines up to the end of the range, summed per assigned rep. */
        var ar = await LedgerPosting.AccountIdAsync(_db, LedgerPosting.ReceivableCode) ?? 0;
        var lines = await _db.JournalEntryLines.AsNoTracking()
            .Where(l => l.AccountId == ar && l.PartyUserId != null &&
                        l.Entry.Status.StatusKey == "POSTED" && l.Entry.EntryDate <= end)
            .GroupBy(l => l.PartyUserId!.Value)
            .Select(g => new { party = g.Key, net = g.Sum(l => l.DebitAmount - l.CreditAmount) })
            .ToDictionaryAsync(x => x.party, x => x.net);
        var customers = await _db.Parties.AsNoTracking()
            .Where(p => p.User.RoleId == 5 || p.User.RoleId == 7)
            .Select(p => new { p.UserId, rep = p.SalesPersonUserId, p.OpeningBalance })
            .ToListAsync();

        var visits = await _db.CustomerVisits.AsNoTracking()
            .Where(v => v.VisitedAt >= startAt && v.VisitedAt < endAt)
            .GroupBy(v => v.SalesPersonUserId)
            .Select(g => new { rep = (int?)g.Key, n = g.Count() })
            .ToListAsync();

        /* Every active rep appears, even on a quiet month -- a row of zeros is
           information; a missing row looks like a bug. */
        var reps = await _db.Employees.AsNoTracking()
            .Where(e => e.User.Role.RoleKey == OrderWorkflow.RoleSales)
            .Select(e => new { id = e.UserId, name = e.User.FullName, active = e.User.IsActive })
            .ToListAsync();

        var ids = reps.Where(r => r.active).Select(r => (int?)r.id)
            .Concat(orders.Select(x => x.rep)).Concat(invoices.Select(x => x.rep))
            .Concat(returns.Select(x => x.rep)).Concat(collected.Select(x => x.rep))
            .Concat(customers.Select(c => c.rep)).Concat(visits.Select(x => x.rep))
            .Distinct().ToList();

        /* A rep sees his own row and nothing else -- not the others' numbers,
           and not the "No salesperson" line. */
        if (me is not null) ids = new List<int?> { me };

        /* The role is shown beside the name: live data has customers assigned
           to an order-desk clerk and to the accountant (HANDOFF D5), and a row
           that says so is how the owner finds them. */
        var names = await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FullName, role = u.Role.RoleName })
            .ToDictionaryAsync(u => u.UserId);

        var rows = ids.Select(id =>
        {
            var mine = customers.Where(c => c.rep == id).ToList();
            var inv = invoices.Where(x => x.rep == id).Sum(x => x.value);
            var ret = returns.Where(x => x.rep == id).Sum(x => x.value);
            return new RepRow(
                id,
                id is null ? "No salesperson (counter, direct, unassigned)"
                           : names.TryGetValue(id.Value, out var n) ? n.FullName : $"User {id}",
                id is null ? null : names.TryGetValue(id.Value, out var n2) ? n2.role : null,
                mine.Count,
                orders.Where(x => x.rep == id).Sum(x => x.n),
                orders.Where(x => x.rep == id).Sum(x => x.value),
                invoices.Where(x => x.rep == id).Sum(x => x.n),
                inv, ret, inv - ret,
                collected.Where(x => x.rep == id).Sum(x => x.value),
                mine.Sum(c => c.OpeningBalance + lines.GetValueOrDefault(c.UserId)),
                visits.Where(x => x.rep == id).Sum(x => x.n));
        })
        /* A departed rep with nothing in the range is not worth a row. */
        .Where(r => r.repId is null
                    ? r.orders + r.invoices + r.customers > 0 || r.returns != 0 || r.collected != 0 || r.outstanding != 0
                    : reps.Any(x => x.id == r.repId && x.active) || r.orders + r.invoices + r.visits > 0 || r.collected != 0 || r.outstanding != 0)
        .OrderBy(r => r.repId is null).ThenByDescending(r => r.netSales).ThenBy(r => r.repName)
        .ToList();

        var totals = new RepRow(null, "Total", null, rows.Sum(r => r.customers), rows.Sum(r => r.orders),
            rows.Sum(r => r.orderValue), rows.Sum(r => r.invoices), rows.Sum(r => r.invoiced),
            rows.Sum(r => r.returns), rows.Sum(r => r.netSales), rows.Sum(r => r.collected),
            rows.Sum(r => r.outstanding), rows.Sum(r => r.visits));

        return new SalesByRep(start, end, me is not null, rows.Count, totals, rows);
    }

    [HttpGet("sales-by-rep/pdf")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByRepPdf([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var (doc, file, _) = await SalesByRepDoc(from, to);
            Response.Headers.ContentDisposition = $"inline; filename=\"{file}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex) { return Fail(ex, "render sales by salesperson"); }
    }

    [HttpPost("sales-by-rep/pdf")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByRepArchive([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var (doc, file, key) = await SalesByRepDoc(from, to);
            return await Archive("report.sales-by-rep", key, doc, file);
        }
        catch (Exception ex) { return Fail(ex, "archive sales by salesperson"); }
    }

    private async Task<(DocumentPdf.Data Doc, string File, string Key)> SalesByRepDoc(DateOnly? from, DateOnly? to)
    {
        var r = await BuildSalesByRep(from, to);
        var c = await ReportKit.LetterHeadAsync(_db);
        var cur = c.CurrencySymbol;
        var doc = new DocumentPdf.Data(
            Company: c,
            Title: "Sales by Salesperson",
            DocNo: null, StatusName: null, Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", DocumentPdf.Day(r.from)),
                new DocumentPdf.Fact("To", DocumentPdf.Day(r.to)),
                new DocumentPdf.Fact("Salespeople", r.count.ToString()),
                new DocumentPdf.Fact("Net Sales", DocumentPdf.Money(r.totals.netSales)),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Salesperson", 3.2),
                new DocumentPdf.Col("Orders", 1.0, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Invoiced", 1.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Returns", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Net Sales", 1.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Collected", 1.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Outstanding", 1.9, DocumentPdf.Align.Right),
            },
            Rows: r.items.Select(x => new DocumentPdf.Row(new[]
            {
                x.repRole is null or "Sales" ? x.repName : $"{x.repName} ({x.repRole})", x.orders.ToString(), DocumentPdf.Money(x.invoiced), ReportKit.Zero(x.returns),
                DocumentPdf.Money(x.netSales), ReportKit.Zero(x.collected), DocumentPdf.Money(x.outstanding)
            }, Sub: $"{x.invoices} invoices  ·  {x.customers} customers  ·  {x.visits} visits")).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Invoiced", DocumentPdf.Money(r.totals.invoiced, cur)),
                new DocumentPdf.Total("Returns", DocumentPdf.Money(-r.totals.returns, cur), Colour: DocumentPdf.Danger),
                new DocumentPdf.Total("Collected", DocumentPdf.Money(r.totals.collected, cur), Colour: DocumentPdf.Success),
                new DocumentPdf.Total("Outstanding (end of range)", DocumentPdf.Money(r.totals.outstanding, cur)),
                new DocumentPdf.Total("Net Sales", DocumentPdf.Money(r.totals.netSales, cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Invoiced = invoices on the rep's orders. Collected = confirmed collections on his orders. " +
                      "Outstanding = what his assigned customers owe at the end of the range, from the ledger.",
            PreparedBy: null,
            EmptyMessage: "No sales in this range.");
        return (doc, $"sales-by-salesperson-{r.from:yyyy-MM-dd}-to-{r.to:yyyy-MM-dd}.pdf",
            $"{r.from:yyyy-MM-dd}:{r.to:yyyy-MM-dd}:{RepScope()?.ToString() ?? "all"}");
    }

    [HttpGet("sales-by-rep/export")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByRepExport([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        try
        {
            var r = await BuildSalesByRep(from, to);
            var bytes = XlsxWriter.FromJson("Sales by Salesperson", ReportKit.ToJson(r.items), new[]
            {
                new XlsxWriter.Column("Salesperson", "repName", XlsxWriter.CellKind.Text, 34),
                new XlsxWriter.Column("Role", "repRole", XlsxWriter.CellKind.Text, 18),
                new XlsxWriter.Column("Customers", "customers", XlsxWriter.CellKind.Integer),
                new XlsxWriter.Column("Orders", "orders", XlsxWriter.CellKind.Integer),
                new XlsxWriter.Column("Order Value", "orderValue", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Invoices", "invoices", XlsxWriter.CellKind.Integer),
                new XlsxWriter.Column("Invoiced", "invoiced", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Returns", "returns", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Net Sales", "netSales", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Collected", "collected", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Outstanding", "outstanding", XlsxWriter.CellKind.Money),
                new XlsxWriter.Column("Visits", "visits", XlsxWriter.CellKind.Integer),
            });
            return File(bytes, XlsxWriter.ContentType, $"sales-by-salesperson-{r.from:yyyy-MM-dd}-to-{r.to:yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex) { return Fail(ex, "export sales by salesperson"); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  SALES BY PRODUCT
    // ══════════════════════════════════════════════════════════════════

    private sealed record ProductLine(
        int productId, string sku, string name, string category, string brand,
        int units, decimal gross, int invoices, decimal cost,
        int returnedUnits, decimal returnedValue);

    [HttpGet("sales-by-product")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByProductReport(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? categoryId, [FromQuery] string? q)
    {
        try { return Ok(await BuildSalesByProduct(from, to, categoryId, q)); }
        catch (Exception ex) { return Fail(ex, "build sales by product"); }
    }

    /// <summary>
    /// Units, sales, average price and returns per product, and rolled up per
    /// category. Sales = invoice line totals (what the customer was charged);
    /// returns = approved/posted return lines at the price the customer paid;
    /// average price = sales / units. For the Super Admin only: cost of the
    /// units kept (unit cost captured on each invoice line, less the returned
    /// units at that product's average cost in the range) and the profit.
    /// A rep sees the products on his own invoices only.
    /// </summary>
    private async Task<object> BuildSalesByProduct(DateOnly? from, DateOnly? to, int? categoryId, string? q)
    {
        var (start, end) = ReportKit.Range(from, to);
        var me = RepScope();
        var seesCost = SeesCost();

        var lines = _db.SalesInvoiceItems.AsNoTracking()
            .Where(l => l.Invoice.InvoiceDate >= start && l.Invoice.InvoiceDate <= end &&
                        !NotASale.Contains(l.Invoice.Status.StatusKey));
        var rets = _db.SalesReturnItems.AsNoTracking()
            .Where(x => x.Return.ReturnDate >= start && x.Return.ReturnDate <= end &&
                        CountedReturns.Contains(x.Return.Status.StatusKey));

        if (me is not null)
        {
            lines = lines.Where(l => l.Invoice.CreatedByUserId == me ||
                                     (l.Invoice.Order != null && l.Invoice.Order.SalesPersonUserId == me));
            rets = rets.Where(x => x.Return.Invoice != null &&
                                   (x.Return.Invoice.CreatedByUserId == me ||
                                    (x.Return.Invoice.Order != null && x.Return.Invoice.Order.SalesPersonUserId == me)));
        }
        if (categoryId is not null)
        {
            lines = lines.Where(l => l.Product.CategoryId == categoryId);
            rets = rets.Where(x => x.Product.CategoryId == categoryId);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            lines = lines.Where(l => l.Product.ProductName.ToLower().Contains(term) || l.Product.Sku.ToLower().Contains(term));
            rets = rets.Where(x => x.Product.ProductName.ToLower().Contains(term) || x.Product.Sku.ToLower().Contains(term));
        }

        var sold = await lines
            .GroupBy(l => l.ProductId)
            .Select(g => new
            {
                productId = g.Key,
                units = g.Sum(l => l.Quantity),
                gross = g.Sum(l => l.LineTotal),
                invoices = g.Select(l => l.InvoiceId).Distinct().Count(),
                cost = g.Sum(l => l.Quantity * l.UnitCost)
            })
            .ToListAsync();

        var back = await rets
            .GroupBy(x => x.ProductId)
            .Select(g => new { productId = g.Key, units = g.Sum(x => x.Quantity), value = g.Sum(x => x.Quantity * x.UnitPrice) })
            .ToListAsync();

        var productIds = sold.Select(s => s.productId).Union(back.Select(b => b.productId)).ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId))
            .Select(p => new { p.ProductId, p.Sku, p.ProductName, category = p.Category.CategoryName, brand = p.Brand.BrandName })
            .ToDictionaryAsync(p => p.ProductId);

        var merged = productIds.Select(id =>
        {
            var s = sold.FirstOrDefault(x => x.productId == id);
            var b = back.FirstOrDefault(x => x.productId == id);
            var p = products[id];
            return new ProductLine(id, p.Sku, p.ProductName, p.category ?? "Uncategorised", p.brand ?? "",
                s?.units ?? 0, s?.gross ?? 0m, s?.invoices ?? 0, s?.cost ?? 0m, b?.units ?? 0, b?.value ?? 0m);
        }).ToList();

        /* The figures anybody may see, and -- for the Super Admin only, as a
           separate shape rather than a zeroed field -- cost and profit. */
        object Shape(ProductLine l)
        {
            var netUnits = l.units - l.returnedUnits;
            var net = l.gross - l.returnedValue;
            var avg = l.units == 0 ? 0m : Math.Round(l.gross / l.units, 2);
            if (!seesCost)
                return new
                {
                    id = l.productId, l.sku, l.name, l.category, l.brand, l.invoices,
                    l.units, l.returnedUnits, netUnits, grossSales = l.gross, l.returnedValue,
                    netSales = net, averagePrice = avg
                };
            /* Returned units carry no cost of their own; they are taken back at
               the product's average invoice cost in the range. */
            var unitCost = l.units == 0 ? 0m : l.cost / l.units;
            var cost = Math.Round(l.cost - l.returnedUnits * unitCost, 2);
            return new
            {
                id = l.productId, l.sku, l.name, l.category, l.brand, l.invoices,
                l.units, l.returnedUnits, netUnits, grossSales = l.gross, l.returnedValue,
                netSales = net, averagePrice = avg,
                cost, profit = net - cost,
                marginPercent = net == 0 ? 0m : Math.Round(100 * (net - cost) / net, 1)
            };
        }

        var ordered = merged.OrderByDescending(l => l.gross - l.returnedValue).ThenBy(l => l.name).ToList();

        var byCategory = merged.GroupBy(l => l.category)
            .OrderByDescending(g => g.Sum(l => l.gross - l.returnedValue))
            .Select(g =>
        {
            var gross = g.Sum(l => l.gross);
            var ret = g.Sum(l => l.returnedValue);
            var units = g.Sum(l => l.units);
            var retUnits = g.Sum(l => l.returnedUnits);
            var costKept = g.Sum(l => l.units == 0 ? 0m : l.cost - l.returnedUnits * (l.cost / l.units));
            return seesCost
                ? (object)new
                {
                    category = g.Key, products = g.Count(), units, returnedUnits = retUnits, netUnits = units - retUnits,
                    grossSales = gross, returnedValue = ret, netSales = gross - ret,
                    cost = Math.Round(costKept, 2), profit = Math.Round(gross - ret - costKept, 2)
                }
                : new
                {
                    category = g.Key, products = g.Count(), units, returnedUnits = retUnits, netUnits = units - retUnits,
                    grossSales = gross, returnedValue = ret, netSales = gross - ret
                };
        }).ToList();

        var totalGross = merged.Sum(l => l.gross);
        var totalRet = merged.Sum(l => l.returnedValue);
        var totalCost = Math.Round(merged.Sum(l => l.units == 0 ? 0m : l.cost - l.returnedUnits * (l.cost / l.units)), 2);

        /* A dictionary rather than an anonymous object so "cost" and "profit"
           are ABSENT for everybody but the Super Admin -- not null, not zero. */
        var answer = new Dictionary<string, object?>
        {
            ["from"] = start, ["to"] = end,
            ["scopedToRep"] = me is not null,
            ["showsCost"] = seesCost,
            ["count"] = merged.Count,
            ["units"] = merged.Sum(l => l.units),
            ["returnedUnits"] = merged.Sum(l => l.returnedUnits),
            ["grossSales"] = totalGross,
            ["returnedValue"] = totalRet,
            ["netSales"] = totalGross - totalRet,
        };
        if (seesCost)
        {
            answer["cost"] = totalCost;
            answer["profit"] = totalGross - totalRet - totalCost;
        }
        answer["categories"] = await _db.Categories.AsNoTracking().OrderBy(c => c.CategoryName)
            .Select(c => new { id = c.CategoryId, name = c.CategoryName }).ToListAsync();
        answer["byCategory"] = byCategory;
        answer["items"] = ordered.Select(Shape).ToList();
        return answer;
    }

    [HttpGet("sales-by-product/pdf")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByProductPdf(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? categoryId, [FromQuery] string? q)
    {
        try
        {
            var (doc, file, _) = await SalesByProductDoc(from, to, categoryId, q);
            Response.Headers.ContentDisposition = $"inline; filename=\"{file}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex) { return Fail(ex, "render sales by product"); }
    }

    [HttpPost("sales-by-product/pdf")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByProductArchive(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? categoryId, [FromQuery] string? q)
    {
        try
        {
            var (doc, file, key) = await SalesByProductDoc(from, to, categoryId, q);
            return await Archive("report.sales-by-product", key, doc, file);
        }
        catch (Exception ex) { return Fail(ex, "archive sales by product"); }
    }

    private async Task<(DocumentPdf.Data Doc, string File, string Key)> SalesByProductDoc(
        DateOnly? from, DateOnly? to, int? categoryId, string? q)
    {
        var j = ReportKit.ToJson(await BuildSalesByProduct(from, to, categoryId, q));
        var seesCost = j.GetProperty("showsCost").GetBoolean();
        var c = await ReportKit.LetterHeadAsync(_db);
        var cur = c.CurrencySymbol;
        decimal D(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDecimal() : 0m;
        string S(System.Text.Json.JsonElement e, string n) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        var from2 = DateOnly.Parse(S(j, "from"));
        var to2 = DateOnly.Parse(S(j, "to"));

        var cols = new List<DocumentPdf.Col>
        {
            new("Item", 3.6),
            new("Units", 1.0, DocumentPdf.Align.Right),
            new("Returned", 1.1, DocumentPdf.Align.Right),
            new("Avg Price", 1.6, DocumentPdf.Align.Right),
            new("Net Sales", 1.9, DocumentPdf.Align.Right),
        };
        if (seesCost)
        {
            cols.Add(new("Cost", 1.8, DocumentPdf.Align.Right));
            cols.Add(new("Profit", 1.8, DocumentPdf.Align.Right));
        }

        var rows = j.GetProperty("items").EnumerateArray().Select(r =>
        {
            var cells = new List<string>
            {
                S(r, "name"), D(r, "units").ToString("N0"), ReportKit.Zero(D(r, "returnedUnits")).Replace(".00", ""),
                DocumentPdf.Money(D(r, "averagePrice")), DocumentPdf.Money(D(r, "netSales"))
            };
            if (seesCost) { cells.Add(DocumentPdf.Money(D(r, "cost"))); cells.Add(DocumentPdf.Money(D(r, "profit"))); }
            return new DocumentPdf.Row(cells, Sub: $"{S(r, "sku")}  ·  {S(r, "category")}");
        }).ToList();

        var totals = new List<DocumentPdf.Total>
        {
            new("Units sold", D(j, "units").ToString("N0")),
            new("Sales", DocumentPdf.Money(D(j, "grossSales"), cur)),
            new("Returns", DocumentPdf.Money(-D(j, "returnedValue"), cur), Colour: DocumentPdf.Danger),
        };
        if (seesCost)
        {
            totals.Add(new("Cost of units kept", DocumentPdf.Money(D(j, "cost"), cur)));
            totals.Add(new("Profit", DocumentPdf.Money(D(j, "profit"), cur), Colour: DocumentPdf.Success));
        }
        totals.Add(new("Net Sales", DocumentPdf.Money(D(j, "netSales"), cur), Emphasis: true));

        var catCols = new List<DocumentPdf.Col>
        {
            new("Category", 3.6), new("Items", 1.0, DocumentPdf.Align.Right), new("Units", 1.2, DocumentPdf.Align.Right),
            new("Returns", 1.8, DocumentPdf.Align.Right), new("Net Sales", 2.0, DocumentPdf.Align.Right)
        };
        if (seesCost) catCols.Add(new("Profit", 2.0, DocumentPdf.Align.Right));
        var catRows = j.GetProperty("byCategory").EnumerateArray().Select(r =>
        {
            var cells = new List<string>
            {
                S(r, "category"), D(r, "products").ToString("N0"), D(r, "units").ToString("N0"),
                ReportKit.Zero(D(r, "returnedValue")), DocumentPdf.Money(D(r, "netSales"))
            };
            if (seesCost) cells.Add(DocumentPdf.Money(D(r, "profit")));
            return new DocumentPdf.Row(cells);
        }).ToList();

        var doc = new DocumentPdf.Data(
            Company: c,
            Title: "Sales by Product",
            DocNo: null, StatusName: null, Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", DocumentPdf.Day(from2)),
                new DocumentPdf.Fact("To", DocumentPdf.Day(to2)),
                new DocumentPdf.Fact("Items", D(j, "count").ToString("N0")),
                new DocumentPdf.Fact("Net Sales", DocumentPdf.Money(D(j, "netSales"))),
            },
            Columns: cols, Rows: rows, Totals: totals,
            Notes: null,
            Footnote: "Sales are invoice line totals; returns are approved or posted returns at the price paid. " +
                      (seesCost ? "Cost is the unit cost captured on each invoice line." : ""),
            PreparedBy: null,
            EmptyMessage: "Nothing sold in this range.",
            More: new[] { new DocumentPdf.Section("By category", catCols, catRows) });

        return (doc, $"sales-by-product-{from2:yyyy-MM-dd}-to-{to2:yyyy-MM-dd}.pdf",
            $"{from2:yyyy-MM-dd}:{to2:yyyy-MM-dd}:{categoryId?.ToString() ?? "all"}:{RepScope()?.ToString() ?? "all"}:{(seesCost ? "c" : "n")}");
    }

    [HttpGet("sales-by-product/export")]
    [Authorize(Roles = "super-admin,accountant,sales")]
    public async Task<IActionResult> SalesByProductExport(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? categoryId, [FromQuery] string? q)
    {
        try
        {
            var j = ReportKit.ToJson(await BuildSalesByProduct(from, to, categoryId, q));
            var seesCost = j.GetProperty("showsCost").GetBoolean();
            var cols = new List<XlsxWriter.Column>
            {
                new("SKU", "sku", XlsxWriter.CellKind.Text, 16),
                new("Item", "name", XlsxWriter.CellKind.Text, 36),
                new("Category", "category", XlsxWriter.CellKind.Text, 20),
                new("Brand", "brand", XlsxWriter.CellKind.Text, 16),
                new("Invoices", "invoices", XlsxWriter.CellKind.Integer),
                new("Units Sold", "units", XlsxWriter.CellKind.Integer),
                new("Units Returned", "returnedUnits", XlsxWriter.CellKind.Integer),
                new("Net Units", "netUnits", XlsxWriter.CellKind.Integer),
                new("Sales", "grossSales", XlsxWriter.CellKind.Money),
                new("Returns", "returnedValue", XlsxWriter.CellKind.Money),
                new("Net Sales", "netSales", XlsxWriter.CellKind.Money),
                new("Average Price", "averagePrice", XlsxWriter.CellKind.Money),
            };
            if (seesCost)
            {
                cols.Add(new("Cost", "cost", XlsxWriter.CellKind.Money));
                cols.Add(new("Profit", "profit", XlsxWriter.CellKind.Money));
                cols.Add(new("Margin %", "marginPercent", XlsxWriter.CellKind.Percent));
            }
            var catCols = new List<XlsxWriter.Column>
            {
                new("Category", "category", XlsxWriter.CellKind.Text, 24),
                new("Items", "products", XlsxWriter.CellKind.Integer),
                new("Units Sold", "units", XlsxWriter.CellKind.Integer),
                new("Units Returned", "returnedUnits", XlsxWriter.CellKind.Integer),
                new("Sales", "grossSales", XlsxWriter.CellKind.Money),
                new("Returns", "returnedValue", XlsxWriter.CellKind.Money),
                new("Net Sales", "netSales", XlsxWriter.CellKind.Money),
            };
            if (seesCost)
            {
                catCols.Add(new("Cost", "cost", XlsxWriter.CellKind.Money));
                catCols.Add(new("Profit", "profit", XlsxWriter.CellKind.Money));
            }
            var bytes = XlsxWriter.FromSheets(new[]
            {
                new XlsxWriter.SheetSpec("By product", cols, j.GetProperty("items")),
                new XlsxWriter.SheetSpec("By category", catCols, j.GetProperty("byCategory")),
            });
            return File(bytes, XlsxWriter.ContentType,
                $"sales-by-product-{j.GetProperty("from").GetString()}-to-{j.GetProperty("to").GetString()}.xlsx");
        }
        catch (Exception ex) { return Fail(ex, "export sales by product"); }
    }

    // ══════════════════════════════════════════════════════════════════
    //  AR AGING -- SEND REMINDERS
    // ══════════════════════════════════════════════════════════════════

    private sealed record Overdue(
        int customerId, string customerName, string code, string? phone, int? repId, string? repName,
        decimal outstanding, decimal overdue, int maxDays, int overdueInvoices, string? oldestInvoice);

    /// <summary>
    /// What each customer owes and how late, from open invoices aged on their
    /// DUE date -- the same arithmetic as GET /reports/aging/customer, so the
    /// reminder says what the report on screen says.
    /// </summary>
    private async Task<List<Overdue>> OverdueCustomers(DateOnly cutoff, IReadOnlyCollection<int>? onlyThese)
    {
        var q = _db.SalesInvoices.AsNoTracking().Where(i => i.InvoiceDate <= cutoff);
        if (onlyThese is { Count: > 0 }) q = q.Where(i => onlyThese.Contains(i.CustomerUserId));

        var rows = await q
            .Select(i => new
            {
                i.CustomerUserId,
                name = i.CustomerUser.DisplayName ?? i.CustomerUser.LegalName,
                code = i.CustomerUser.PartyCode,
                phone = i.CustomerUser.User.Phone,
                rep = i.CustomerUser.SalesPersonUserId,
                repName = i.CustomerUser.SalesPersonUser != null ? i.CustomerUser.SalesPersonUser.User.FullName : null,
                i.InvoiceNo, i.DueDate,
                total = i.TotalAmount,
                paid = i.VoucherAllocations.Where(v => v.Voucher.Status.StatusKey == "POSTED").Sum(v => (decimal?)v.Amount) ?? 0m
            })
            .ToListAsync();

        return rows.Where(r => r.total - r.paid > 0)
            .GroupBy(r => r.CustomerUserId)
            .Select(g =>
            {
                var f = g.First();
                var late = g.Where(x => x.DueDate < cutoff).ToList();
                return new Overdue(
                    g.Key, f.name, f.code, f.phone, f.rep, f.repName,
                    g.Sum(x => x.total - x.paid),
                    late.Sum(x => x.total - x.paid),
                    late.Count == 0 ? 0 : late.Max(x => cutoff.DayNumber - x.DueDate.DayNumber),
                    late.Count,
                    late.OrderBy(x => x.DueDate).Select(x => x.InvoiceNo).FirstOrDefault());
            })
            .ToList();
    }

    /// <summary>
    /// "Send Reminders" -- it used to show a toast saying an SMS provider was
    /// needed. There is no SMS provider, and there does not need to be one: the
    /// people who chase the money are already in the app.
    ///
    ///   · each customer's SALESPERSON gets one in-app + push notification
    ///     listing his customers that are overdue, with the amount and how many
    ///     days -- he is the one who walks in and asks;
    ///   · the Super Admin and the accountant get one summary (everybody except
    ///     whoever pressed the button);
    ///   · every customer reminded is written to the activity log, with what was
    ///     said and to whom;
    ///   · customers with no assigned rep are named back, so somebody assigns
    ///     one rather than the reminder silently going nowhere.
    ///
    /// The customer himself is reached over WhatsApp, from the screen (a wa.me
    /// link per row, the same handoff the bill uses): nothing leaves the
    /// business's phone until a person presses Send.
    ///
    /// Body: { customerIds?: [..], asOf?: date }. No ids = every customer with
    /// anything past due. Named customers are reminded if they owe anything,
    /// even if it is not yet due (someone chose them).
    /// </summary>
    [HttpPost("aging/customer/reminders")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SendReminders([FromBody] ReminderRequest? body)
    {
        try
        {
            var cutoff = body?.AsOf ?? Today();
            var chosen = body?.CustomerIds?.Distinct().ToList();
            var all = await OverdueCustomers(cutoff, chosen);
            var list = (chosen is { Count: > 0 }
                    ? all.Where(c => c.outstanding > 0)
                    : all.Where(c => c.overdue > 0))
                .OrderByDescending(c => c.overdue).ThenByDescending(c => c.outstanding).ToList();

            if (list.Count == 0)
                return BadRequest(new { message = chosen is { Count: > 0 } ? "None of those customers owes anything." : "Nobody is overdue -- there is no one to remind." });

            static string Line(Overdue c) => c.overdue > 0
                ? $"{c.customerName} PKR {c.overdue:N0} ({c.maxDays} days late)"
                : $"{c.customerName} PKR {c.outstanding:N0} (not yet due)";

            /* One notification per rep, naming his customers -- not one per
               customer, which would bury his phone on the day it matters. */
            var reps = 0;
            foreach (var g in list.Where(c => c.repId is not null).GroupBy(c => c.repId!.Value))
            {
                /* Whoever pressed the button has just read the list. */
                if (g.Key == CurrentUserId()) continue;
                var mine = g.ToList();
                var total = mine.Sum(c => c.overdue > 0 ? c.overdue : c.outstanding);
                await _push.NotifyAsync(g.Key, NotificationKinds.PaymentReminder,
                    $"Payment reminder from {CurrentUserName()}",
                    $"{mine.Count} of your customer{(mine.Count == 1 ? "" : "s")} owe PKR {total:N0}: " +
                    string.Join("; ", mine.Take(5).Select(Line)) + (mine.Count > 5 ? $"; and {mine.Count - 5} more." : ".") +
                    " Please visit or call.",
                    url: mine.Count == 1 ? $"/parties/{mine[0].customerId}" : "/parties/customers");
                reps++;
            }

            var noRep = list.Where(c => c.repId is null).ToList();
            var overdueTotal = list.Sum(c => c.overdue);
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.PaymentReminder,
                $"Payment reminders sent by {CurrentUserName()}",
                $"{list.Count} customer{(list.Count == 1 ? "" : "s")}, PKR {overdueTotal:N0} overdue, {reps} rep{(reps == 1 ? "" : "s")} told. " +
                $"Worst: {string.Join("; ", list.Take(3).Select(Line))}." +
                (noRep.Count > 0 ? $" No rep assigned: {string.Join(", ", noRep.Take(3).Select(c => c.customerName))}." : ""),
                url: "/reports/aging/customer",
                exceptUserId: CurrentUserId());

            foreach (var c in list)
                await Log("PAYMENT_REMINDER_SENT", "Party", c.code,
                    (c.overdue > 0
                        ? $"PKR {c.overdue:N2} overdue, {c.maxDays} days, oldest {c.oldestInvoice}"
                        : $"PKR {c.outstanding:N2} outstanding, not yet due") +
                    $"; reminded {(c.repName is null ? "nobody -- no rep assigned" : c.repName)} and the back office", 1);

            return Ok(new
            {
                reminded = list.Count,
                repsNotified = reps,
                withoutRep = noRep.Select(c => new { id = c.customerId, name = c.customerName }),
                customers = list.Select(c => new
                {
                    id = c.customerId, name = c.customerName, c.phone, c.overdue, c.outstanding,
                    daysOverdue = c.maxDays, salesPerson = c.repName
                }),
                message = $"Reminders sent for {list.Count} customer{(list.Count == 1 ? "" : "s")} " +
                          $"(PKR {overdueTotal:N0} overdue) to {reps} salesperson{(reps == 1 ? "" : "s")} and the back office." +
                          (noRep.Count > 0 ? $" {noRep.Count} ha{(noRep.Count == 1 ? "s" : "ve")} no rep assigned." : "")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "send payment reminders");
        }
    }

    /// <summary>
    /// A WhatsApp reminder was opened for a customer. wa.me hands the message
    /// to the phone and tells the server nothing, so the screen reports it here
    /// -- "record what was sent" covers the reminders that went to the customer
    /// himself too. It records that the message was prepared and opened; only
    /// the person holding the phone knows whether Send was pressed.
    /// </summary>
    [HttpPost("aging/customer/reminders/whatsapp")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> LogWhatsAppReminder([FromBody] WhatsAppReminderRequest body)
    {
        try
        {
            var c = (await OverdueCustomers(body.AsOf ?? Today(), new[] { body.CustomerId })).FirstOrDefault();
            if (c is null) return BadRequest(new { message = "That customer owes nothing." });
            if (string.IsNullOrWhiteSpace(c.phone))
                return BadRequest(new { message = $"{c.customerName} has no phone number on file." });

            await Log("WHATSAPP_REMINDER_OPENED", "Party", c.code,
                $"To {c.phone}: PKR {(c.overdue > 0 ? c.overdue : c.outstanding):N2}" +
                (c.overdue > 0 ? $", {c.maxDays} days overdue" : ", not yet due") +
                (string.IsNullOrWhiteSpace(body.Language) ? "" : $" ({body.Language})"), 1);
            return Ok(new { message = $"WhatsApp reminder to {c.customerName} recorded." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "record the WhatsApp reminder");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  DEAD STOCK -- PLAN CLEARANCE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// "Plan Clearance" -- it used to show a toast. It now does the one useful
    /// thing a person can act on today: a clearance SHEET (.xlsx) of every dead
    /// line with a suggested clearance price at the discount chosen, the stock
    /// value at the current and at the clearance price, and -- for the Super
    /// Admin only, since what an item cost is his alone -- the cost, and the
    /// lines where the clearance price would sell below it (optionally held at
    /// cost). The Super Admin is told a plan was drawn up, by whom, and at what
    /// discount; changing prices stays his decision on the product screen.
    ///
    /// Why a sheet and not a button that re-prices the catalogue: a clearance
    /// is a negotiation with a few buyers, line by line, and the price parts of
    /// an item (cost, duty, FS, two margins -- migration 26) are the Super
    /// Admin's to change. A sheet he can print, mark up and hand to a buyer is
    /// the real tool; silently re-pricing thirty items at once is not.
    ///
    /// "Dead" is exactly what GET /reports/dead-stock means: stock on hand and
    /// no outward movement in the window.
    /// </summary>
    [HttpPost("dead-stock/clearance")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> ClearanceSheet([FromBody] ClearanceRequest body)
    {
        try
        {
            var days = body.Days is < 1 or > 3650 ? 90 : body.Days;
            var discount = Math.Round(body.DiscountPercent, 1);
            if (discount is <= 0 or >= 100)
                return BadRequest(new { message = "The clearance discount must be more than 0% and less than 100%." });

            var seesCost = SeesCost();
            var floorAtCost = seesCost && body.FloorAtCost;
            var since = Today().AddDays(-days).ToDateTime(TimeOnly.MinValue);

            var rows = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new
                {
                    p.ProductId, p.Sku, p.ProductName,
                    category = p.Category.CategoryName,
                    brand = p.Brand.BrandName,
                    p.CostPrice, p.SalePrice,
                    onHand = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    lastOut = p.StockMovements.Where(m => m.Quantity < 0).Max(m => (DateTime?)m.MovedAt),
                    soldInWindow = p.StockMovements.Where(m => m.Quantity < 0 && m.MovedAt >= since).Sum(m => (int?)-m.Quantity) ?? 0
                })
                .ToListAsync();

            var dead = rows.Where(r => r.onHand > 0 && r.soldInWindow == 0).ToList();
            if (dead.Count == 0)
                return BadRequest(new { message = $"Nothing has sat unsold for {days} days -- there is nothing to clear." });

            var today = Today();
            var lines = dead.Select(r =>
            {
                /* Whole rupees: nobody quotes a buyer Rs 1,234.56. */
                var suggested = Math.Round(r.SalePrice * (1 - discount / 100m), 0, MidpointRounding.AwayFromZero);
                var held = floorAtCost && suggested < r.CostPrice;
                var price = held ? Math.Ceiling(r.CostPrice) : suggested;
                var daysIdle = r.lastOut is null ? (int?)null : today.DayNumber - DateOnly.FromDateTime(r.lastOut.Value).DayNumber;
                return new
                {
                    sku = r.Sku, name = r.ProductName, category = r.category ?? "", brand = r.brand ?? "",
                    onHand = r.onHand,
                    lastSold = r.lastOut is null ? null : (DateOnly?)DateOnly.FromDateTime(r.lastOut.Value),
                    daysIdle,
                    salePrice = r.SalePrice,
                    discountPercent = r.SalePrice == 0 ? 0 : Math.Round(100 * (r.SalePrice - price) / r.SalePrice, 1),
                    clearancePrice = price,
                    currentValue = r.onHand * r.SalePrice,
                    clearanceValue = r.onHand * price,
                    costPrice = r.CostPrice,
                    costValue = r.onHand * r.CostPrice,
                    belowCost = price < r.CostPrice ? "Below cost" : held ? "Held at cost" : "",
                    note = ""
                };
            })
            .OrderByDescending(x => x.currentValue)
            .ToList();

            var cols = new List<XlsxWriter.Column>
            {
                new("SKU", "sku", XlsxWriter.CellKind.Text, 16),
                new("Item", "name", XlsxWriter.CellKind.Text, 36),
                new("Category", "category", XlsxWriter.CellKind.Text, 18),
                new("Brand", "brand", XlsxWriter.CellKind.Text, 14),
                new("On Hand", "onHand", XlsxWriter.CellKind.Integer),
                new("Last Sold", "lastSold", XlsxWriter.CellKind.Date),
                new("Days Idle", "daysIdle", XlsxWriter.CellKind.Integer),
                new("Sale Price", "salePrice", XlsxWriter.CellKind.Money),
                new("Discount %", "discountPercent", XlsxWriter.CellKind.Percent),
                new("Clearance Price", "clearancePrice", XlsxWriter.CellKind.Money),
                new("Value Now", "currentValue", XlsxWriter.CellKind.Money),
                new("Value at Clearance", "clearanceValue", XlsxWriter.CellKind.Money),
            };
            if (seesCost)
            {
                cols.Add(new("Cost", "costPrice", XlsxWriter.CellKind.Money));
                cols.Add(new("Cost Value", "costValue", XlsxWriter.CellKind.Money));
                cols.Add(new("Against Cost", "belowCost", XlsxWriter.CellKind.Text, 14));
            }
            cols.Add(new("Buyer / Note", "note", XlsxWriter.CellKind.Text, 30));

            var bytes = XlsxWriter.FromJson("Clearance plan", ReportKit.ToJson(lines), cols);

            var valueNow = lines.Sum(l => l.currentValue);
            var valueClear = lines.Sum(l => l.clearanceValue);
            var belowCost = lines.Count(l => l.belowCost == "Below cost");

            await Log("CLEARANCE_PLANNED", "Report", $"dead-stock-{days}d",
                $"{lines.Count} lines, {discount}% off: {valueNow:N0} -> {valueClear:N0}" +
                (seesCost ? $"; {belowCost} below cost{(floorAtCost ? ", floor at cost" : "")}" : ""), 1);

            await _push.NotifyRoleAsync("super-admin", NotificationKinds.ClearancePlanned,
                $"Clearance plan by {CurrentUserName()}",
                $"{lines.Count} items unsold for {days}+ days, {discount}% off: stock worth PKR {valueNow:N0} " +
                $"at sale price would clear for PKR {valueClear:N0}. Prices are unchanged until you change them.",
                url: $"/reports/dead-stock",
                exceptUserId: CurrentUserId());

            return File(bytes, XlsxWriter.ContentType, $"clearance-plan-{days}d-{discount:0.#}pct-{today:yyyy-MM-dd}.xlsx");
        }
        catch (Exception ex)
        {
            return Fail(ex, "draw up the clearance plan");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  shared
    // ══════════════════════════════════════════════════════════════════

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

    // ══════════════════════════ request bodies ══════════════════════════

    public record ReminderRequest(List<int>? CustomerIds, DateOnly? AsOf);

    public record WhatsAppReminderRequest(int CustomerId, DateOnly? AsOf, string? Language);

    public record ClearanceRequest(int Days, decimal DiscountPercent, bool FloorAtCost);
}
