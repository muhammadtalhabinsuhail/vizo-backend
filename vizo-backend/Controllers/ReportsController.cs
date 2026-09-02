using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /reports screens: sales summary, customer and supplier ageing, dead
/// stock, slow movers and top customers.
///
/// Everything here is READ ONLY and computed on the fly -- there is no report
/// cache and no stored aggregate, because a stale number that looks official is
/// worse than a slow query. Every money figure comes from POSTED ledger rows or
/// from invoices; nothing counts a draft.
///
/// Every report can also be rendered to PDF and pushed to the documents
/// Cloudinary account -- see the PDF section at the foot of this file. The
/// renderer calls the SAME action the browser calls rather than re-running a
/// copy of the query, so paper and screen cannot disagree.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/reports")]
[ApiController]
[Authorize(Policy = "Staff")]
public class ReportsController : ApiControllerBase
{
    /* Cycle-safe serialisation for the payloads handed to the model. The
       entity graph is heavily self-referencing and a plain Serialize would
       recurse forever. */
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    private readonly GeminiClient _ai;

    public ReportsController(AppDbContext db, IConfiguration cfg,
        ILogger<ReportsController> logger, IWebHostEnvironment env,
        GeminiClient ai)
        : base(db, cfg, logger, env) => _ai = ai;

    // ══════════════════════════════════════════════════════════════════
    //  SALES SUMMARY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("sales-summary")]
    public async Task<IActionResult> SalesSummary(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? locationId)
    {
        try
        {
            var start = from ?? Today().AddDays(-30);
            var end = to ?? Today();

            var q = _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceDate >= start && i.InvoiceDate <= end);
            if (locationId is not null) q = q.Where(i => i.LocationId == locationId);

            var invoices = await q
                .Select(i => new
                {
                    i.InvoiceId,
                    i.InvoiceDate,
                    i.TotalAmount,
                    i.Subtotal,
                    i.DiscountAmount,
                    i.TaxAmount,
                    Location = i.Location.LocationName,
                    Customer = i.CustomerUser.LegalName,
                    Cost = i.SalesInvoiceItems.Sum(l => (decimal?)(l.Quantity * l.UnitCost)) ?? 0m,
                    Units = i.SalesInvoiceItems.Sum(l => (int?)l.Quantity) ?? 0
                })
                .ToListAsync();

            var byDay = invoices
                .GroupBy(i => i.InvoiceDate)
                .Select(g => new
                {
                    date = g.Key,
                    invoices = g.Count(),
                    units = g.Sum(x => x.Units),
                    revenue = g.Sum(x => x.TotalAmount),
                    cost = g.Sum(x => x.Cost),
                    margin = g.Sum(x => x.TotalAmount) - g.Sum(x => x.Cost)
                })
                .OrderBy(x => x.date)
                .ToList();

            var byLocation = invoices
                .GroupBy(i => i.Location)
                .Select(g => new
                {
                    location = g.Key,
                    invoices = g.Count(),
                    revenue = g.Sum(x => x.TotalAmount),
                    cost = g.Sum(x => x.Cost),
                    margin = g.Sum(x => x.TotalAmount) - g.Sum(x => x.Cost)
                })
                .OrderByDescending(x => x.revenue)
                .ToList();

            var revenue = invoices.Sum(i => i.TotalAmount);
            var cost = invoices.Sum(i => i.Cost);

            return Ok(new
            {
                from = start,
                to = end,
                invoiceCount = invoices.Count,
                unitsSold = invoices.Sum(i => i.Units),
                subtotal = invoices.Sum(i => i.Subtotal),
                discount = invoices.Sum(i => i.DiscountAmount),
                tax = invoices.Sum(i => i.TaxAmount),
                revenue,
                cost,
                margin = revenue - cost,
                marginPercent = revenue == 0 ? 0 : Math.Round(100 * (revenue - cost) / revenue, 1),
                averageInvoice = invoices.Count == 0 ? 0 : Math.Round(revenue / invoices.Count, 2),
                byDay,
                byLocation
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the sales summary");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  AGEING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Customer receivables in the usual 0-30 / 31-60 / 61-90 / 90+ buckets,
    /// aged from the invoice DUE date rather than the invoice date -- an invoice
    /// on 60-day terms is not overdue on day 31.
    /// </summary>
    [HttpGet("aging/customer")]
    public async Task<IActionResult> CustomerAging([FromQuery] DateOnly? asOf)
    {
        try
        {
            var cutoff = asOf ?? Today();

            var rows = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceDate <= cutoff)
                .Select(i => new
                {
                    customerId = i.CustomerUserId,
                    customerName = i.CustomerUser.LegalName,
                    creditDays = i.CustomerUser.CreditDays,
                    creditLimit = i.CustomerUser.CreditLimit,
                    i.InvoiceNo,
                    i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var open = rows.Where(r => r.total - r.paid > 0).ToList();

            var byCustomer = open
                .GroupBy(r => new { r.customerId, r.customerName, r.creditDays, r.creditLimit })
                .Select(g =>
                {
                    decimal B(int lo, int hi) => g
                        .Where(x =>
                        {
                            var d = cutoff.DayNumber - x.DueDate.DayNumber;
                            return d >= lo && (hi < 0 || d <= hi);
                        })
                        .Sum(x => x.total - x.paid);

                    var outstanding = g.Sum(x => x.total - x.paid);
                    return new
                    {
                        customerId = g.Key.customerId,
                        customerName = g.Key.customerName,
                        customerInitials = Initials(g.Key.customerName),
                        creditDays = g.Key.creditDays,
                        creditLimit = g.Key.creditLimit,
                        invoiceCount = g.Count(),
                        current = B(int.MinValue, 0),
                        d0_30 = B(1, 30),
                        d31_60 = B(31, 60),
                        d61_90 = B(61, 90),
                        d90plus = B(91, -1),
                        outstanding,
                        overLimit = g.Key.creditLimit > 0 && outstanding > g.Key.creditLimit
                    };
                })
                .OrderByDescending(c => c.outstanding)
                .ToList();

            return Ok(new
            {
                asOf = cutoff,
                customerCount = byCustomer.Count,
                current = byCustomer.Sum(c => c.current),
                d0_30 = byCustomer.Sum(c => c.d0_30),
                d31_60 = byCustomer.Sum(c => c.d31_60),
                d61_90 = byCustomer.Sum(c => c.d61_90),
                d90plus = byCustomer.Sum(c => c.d90plus),
                totalOutstanding = byCustomer.Sum(c => c.outstanding),
                overLimitCount = byCustomer.Count(c => c.overLimit),
                items = byCustomer
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the customer ageing report");
        }
    }

    [HttpGet("aging/supplier")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> SupplierAging([FromQuery] DateOnly? asOf)
    {
        try
        {
            var cutoff = asOf ?? Today();

            var rows = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.InvoiceDate <= cutoff)
                .Select(i => new
                {
                    supplierId = i.SupplierUserId,
                    supplierName = i.SupplierUser.LegalName,
                    creditDays = i.SupplierUser.CreditDays,
                    i.InvoiceNo,
                    i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED")
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .ToListAsync();

            var open = rows.Where(r => r.total - r.paid > 0).ToList();

            var bySupplier = open
                .GroupBy(r => new { r.supplierId, r.supplierName, r.creditDays })
                .Select(g =>
                {
                    decimal B(int lo, int hi) => g
                        .Where(x =>
                        {
                            var d = cutoff.DayNumber - x.DueDate.DayNumber;
                            return d >= lo && (hi < 0 || d <= hi);
                        })
                        .Sum(x => x.total - x.paid);

                    return new
                    {
                        supplierId = g.Key.supplierId,
                        supplierName = g.Key.supplierName,
                        supplierInitials = Initials(g.Key.supplierName),
                        creditDays = g.Key.creditDays,
                        invoiceCount = g.Count(),
                        current = B(int.MinValue, 0),
                        d0_30 = B(1, 30),
                        d31_60 = B(31, 60),
                        d61_90 = B(61, 90),
                        d90plus = B(91, -1),
                        outstanding = g.Sum(x => x.total - x.paid)
                    };
                })
                .OrderByDescending(s => s.outstanding)
                .ToList();

            return Ok(new
            {
                asOf = cutoff,
                supplierCount = bySupplier.Count,
                current = bySupplier.Sum(s => s.current),
                d0_30 = bySupplier.Sum(s => s.d0_30),
                d31_60 = bySupplier.Sum(s => s.d31_60),
                d61_90 = bySupplier.Sum(s => s.d61_90),
                d90plus = bySupplier.Sum(s => s.d90plus),
                totalOutstanding = bySupplier.Sum(s => s.outstanding),
                items = bySupplier
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the supplier ageing report");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  STOCK REPORTS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Stock that has not moved outward at all within `days`. Capital sitting
    /// on a shelf. Products with zero stock are excluded -- nothing to clear.
    /// </summary>
    [HttpGet("dead-stock")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> DeadStock([FromQuery] int days = 90)
    {
        try
        {
            if (days is < 1 or > 3650) days = 90;
            var since = Today().AddDays(-days).ToDateTime(TimeOnly.MinValue);

            var rows = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    category = p.Category.CategoryName,
                    brand = p.Brand.BrandName,
                    costPrice = p.CostPrice,
                    salePrice = p.SalePrice,
                    onHand = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    lastOut = p.StockMovements
                        .Where(m => m.Quantity < 0)
                        .Max(m => (DateTime?)m.MovedAt),
                    soldInWindow = p.StockMovements
                        .Where(m => m.Quantity < 0 && m.MovedAt >= since)
                        .Sum(m => (int?)-m.Quantity) ?? 0
                })
                .ToListAsync();

            var dead = rows
                .Where(r => r.onHand > 0 && r.soldInWindow == 0)
                .Select(r => new
                {
                    r.id, r.sku, r.name, r.category, r.brand,
                    r.costPrice, r.salePrice, r.onHand,
                    lastOut = r.lastOut,
                    daysSinceLastOut = r.lastOut == null ? (int?)null
                        : (Today().DayNumber - DateOnly.FromDateTime(r.lastOut.Value).DayNumber),
                    tiedUpValue = r.onHand * r.costPrice
                })
                .OrderByDescending(r => r.tiedUpValue)
                .ToList();

            return Ok(new
            {
                windowDays = days,
                count = dead.Count,
                tiedUpValue = dead.Sum(d => d.tiedUpValue),
                items = dead
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the dead-stock report");
        }
    }

    /// <summary>
    /// Slow movers: still selling, but at a rate that means months of cover.
    /// The useful number is days-of-cover, not units sold -- 5 units a month is
    /// fine on a 10-unit shelf and terrible on a 500-unit one.
    /// </summary>
    [HttpGet("slow-moving")]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> SlowMoving([FromQuery] int days = 90, [FromQuery] int minCoverDays = 120)
    {
        try
        {
            if (days is < 1 or > 3650) days = 90;
            var since = Today().AddDays(-days).ToDateTime(TimeOnly.MinValue);

            var rows = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    category = p.Category.CategoryName,
                    brand = p.Brand.BrandName,
                    costPrice = p.CostPrice,
                    onHand = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0,
                    soldInWindow = p.StockMovements
                        .Where(m => m.Quantity < 0 && m.MovedAt >= since)
                        .Sum(m => (int?)-m.Quantity) ?? 0
                })
                .ToListAsync();

            var slow = rows
                .Where(r => r.onHand > 0 && r.soldInWindow > 0)
                .Select(r =>
                {
                    var perDay = (double)r.soldInWindow / days;
                    var cover = perDay <= 0 ? int.MaxValue : (int)Math.Round(r.onHand / perDay);
                    return new
                    {
                        r.id, r.sku, r.name, r.category, r.brand,
                        r.costPrice, r.onHand, r.soldInWindow,
                        perDay = Math.Round(perDay, 2),
                        coverDays = cover,
                        tiedUpValue = r.onHand * r.costPrice
                    };
                })
                .Where(r => r.coverDays >= minCoverDays)
                .OrderByDescending(r => r.tiedUpValue)
                .ToList();

            return Ok(new
            {
                windowDays = days,
                minCoverDays,
                count = slow.Count,
                tiedUpValue = slow.Sum(s => s.tiedUpValue),
                items = slow
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the slow-moving report");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  TOP CUSTOMERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("top-customers")]
    public async Task<IActionResult> TopCustomers(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int limit = 20)
    {
        try
        {
            if (limit is < 1 or > 200) limit = 20;
            var start = from ?? Today().AddDays(-365);
            var end = to ?? Today();

            var rows = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceDate >= start && i.InvoiceDate <= end)
                .GroupBy(i => new { i.CustomerUserId, i.CustomerUser.LegalName, City = i.CustomerUser.City.CityName })
                .Select(g => new
                {
                    customerId = g.Key.CustomerUserId,
                    customerName = g.Key.LegalName,
                    city = g.Key.City,
                    invoiceCount = g.Count(),
                    revenue = g.Sum(i => i.TotalAmount),
                    cost = g.Sum(i => i.SalesInvoiceItems.Sum(l => (decimal?)(l.Quantity * l.UnitCost)) ?? 0m),
                    lastInvoice = g.Max(i => i.InvoiceDate)
                })
                .ToListAsync();

            var top = rows
                .Select(r => new
                {
                    r.customerId, r.customerName,
                    customerInitials = Initials(r.customerName),
                    r.city, r.invoiceCount, r.revenue, r.cost,
                    margin = r.revenue - r.cost,
                    marginPercent = r.revenue == 0 ? 0 : Math.Round(100 * (r.revenue - r.cost) / r.revenue, 1),
                    averageInvoice = r.invoiceCount == 0 ? 0 : Math.Round(r.revenue / r.invoiceCount, 2),
                    r.lastInvoice,
                    daysSinceLastInvoice = Today().DayNumber - r.lastInvoice.DayNumber
                })
                .OrderByDescending(r => r.revenue)
                .Take(limit)
                .ToList();

            return Ok(new
            {
                from = start,
                to = end,
                count = top.Count,
                totalRevenue = top.Sum(t => t.revenue),
                totalMargin = top.Sum(t => t.margin),
                items = top
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "build the top-customers report");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  INDEX
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Headline numbers for the /reports landing page, so it shows something
    /// real instead of a menu of links.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        try
        {
            var monthStart = new DateOnly(Today().Year, Today().Month, 1);

            var monthRevenue = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.InvoiceDate >= monthStart)
                .SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

            var receivable = await _db.JournalEntryLines.AsNoTracking()
                .Where(l => l.PartyUserId != null && l.Entry.Status.StatusKey == "POSTED")
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;

            var stockValue = await _db.StockBalances.AsNoTracking()
                .SumAsync(s => (decimal?)(s.Quantity * s.Product.CostPrice)) ?? 0m;

            return Ok(new
            {
                monthRevenue,
                monthInvoices = await _db.SalesInvoices.CountAsync(i => i.InvoiceDate >= monthStart),
                receivable,
                stockValue,
                stockUnits = await _db.StockBalances.SumAsync(s => (int?)s.Quantity) ?? 0,
                activeCustomers = await _db.Parties
                    .CountAsync(p => (p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive),
                activeProducts = await _db.Products.CountAsync(p => p.IsActive),
                openClaims = await _db.Claims.CountAsync(c => c.Stage.IsOpen),
                deliveriesInFlight = await _db.Deliveries.CountAsync(d => d.Status.IsOpen),
                reports = new[]
                {
                    new { key = "sales-summary",  name = "Sales summary",   href = "/reports/sales-summary" },
                    new { key = "aging-customer", name = "Customer ageing", href = "/reports/aging/customer" },
                    new { key = "aging-supplier", name = "Supplier ageing", href = "/reports/aging/supplier" },
                    new { key = "dead-stock",     name = "Dead stock",      href = "/reports/dead-stock" },
                    new { key = "slow-moving",    name = "Slow moving",     href = "/reports/slow-moving" },
                    new { key = "top-customers",  name = "Top customers",   href = "/reports/top-customers" }
                }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the reports overview");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PDF
    // ══════════════════════════════════════════════════════════════════

    /*  Every report can be rendered to PDF and pushed to the "CloudinaryPdfs"
        account, exactly like the bills.

        Before this, the Export PDF / Excel / CSV buttons in the report toolbar
        -- shared by all seven report screens -- called toast.success("Exporting
        PDF…") and did nothing else. Nothing was generated and nothing was ever
        stored anywhere.

        HOW THE PDF CANNOT DRIFT FROM THE SCREEN. Each renderer calls the SAME
        action the browser calls and reads its result, rather than re-running a
        copy of the query. If the ageing buckets change, both change together;
        there is no second implementation to forget about.

        A report has no row anywhere to key a stored file off -- a sales summary
        for August is a document about a date range. So the archive key is a
        fingerprint of the parameters it was run with, which also means re-running
        the same report replaces its file instead of piling up copies.          */

    private static readonly string[] ReportKeys =
    {
        "sales-summary", "aging-customer", "aging-supplier",
        "dead-stock", "slow-moving", "top-customers"
    };

    /// <summary>The report as a PDF, built on request. Print and Preview use this.</summary>
    // ══════════════════════════════════════════════════════════════════
    //  WHY DID SALES FALL
    // ══════════════════════════════════════════════════════════════════

    /*  This endpoint contains NO AI. Not a single call.

        It takes two periods and breaks the difference between them apart, six
        ways, in SQL. Which customers bought less. Which products fell. What was
        out of stock. Whether the price moved. Which rep's number dropped. Which
        cost line jumped.

        That separation is the whole design. Ask a language model "why did sales
        drop" and it will produce a fluent, confident, invented answer. Give it
        THESE numbers and ask it to explain them, and it can only rearrange
        facts. /reports/sales-drop/explain does that second step; this does the
        first, and it is useful on its own with no model configured at all.     */

    /// <summary>
    /// One period against another, broken down into the pieces that can
    /// account for the difference. Defaults to this month against last.
    /// </summary>
    [HttpGet("sales-drop")]
    public async Task<IActionResult> SalesDrop(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] DateOnly? baseFrom, [FromQuery] DateOnly? baseTo)
    {
        try
        {
            var today = Today();

            /* Default: the month so far against the same stretch of last month.
               Comparing a half-finished month against a whole one is the most
               common way a sales report frightens somebody for no reason. */
            var curFrom = from ?? new DateOnly(today.Year, today.Month, 1);
            var curTo = to ?? today;
            var prevFrom = baseFrom ?? curFrom.AddMonths(-1);
            var prevTo = baseTo ?? curTo.AddMonths(-1);

            if (curTo < curFrom || prevTo < prevFrom)
                return BadRequest(new { message = "The end of a period cannot be before its start." });

            var invoices = _db.SalesInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "CANCELLED");

            var cur = invoices.Where(i => i.InvoiceDate >= curFrom && i.InvoiceDate <= curTo);
            var prev = invoices.Where(i => i.InvoiceDate >= prevFrom && i.InvoiceDate <= prevTo);

            var curTotal = await cur.SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;
            var prevTotal = await prev.SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;
            var curCount = await cur.CountAsync();
            var prevCount = await prev.CountAsync();

            // ── by customer ────────────────────────────────────────────────
            var curByCustomer = await cur
                .GroupBy(i => new { i.CustomerUserId, i.CustomerUser.LegalName })
                .Select(g => new { g.Key.CustomerUserId, g.Key.LegalName, amount = g.Sum(i => i.TotalAmount), orders = g.Count() })
                .ToListAsync();
            var prevByCustomer = await prev
                .GroupBy(i => new { i.CustomerUserId, i.CustomerUser.LegalName })
                .Select(g => new { g.Key.CustomerUserId, g.Key.LegalName, amount = g.Sum(i => i.TotalAmount), orders = g.Count() })
                .ToListAsync();

            var customerRows = prevByCustomer
                .Select(pv =>
                {
                    var now = curByCustomer.FirstOrDefault(c => c.CustomerUserId == pv.CustomerUserId);
                    var nowAmt = now?.amount ?? 0m;
                    return new
                    {
                        id = pv.CustomerUserId,
                        name = pv.LegalName,
                        was = pv.amount,
                        now = nowAmt,
                        change = nowAmt - pv.amount,
                        stoppedBuying = now is null
                    };
                })
                .Where(c => c.change < 0)
                .OrderBy(c => c.change)
                .Take(10)
                .ToList();

            // ── by product ─────────────────────────────────────────────────
            var curByProduct = await _db.SalesInvoiceItems.AsNoTracking()
                .Where(l => l.Invoice.Status.StatusKey != "CANCELLED"
                         && l.Invoice.InvoiceDate >= curFrom && l.Invoice.InvoiceDate <= curTo)
                .GroupBy(l => new { l.ProductId, l.Product.ProductName })
                .Select(g => new { g.Key.ProductId, g.Key.ProductName, qty = g.Sum(x => x.Quantity), amount = g.Sum(x => x.LineTotal) })
                .ToListAsync();

            var prevByProduct = await _db.SalesInvoiceItems.AsNoTracking()
                .Where(l => l.Invoice.Status.StatusKey != "CANCELLED"
                         && l.Invoice.InvoiceDate >= prevFrom && l.Invoice.InvoiceDate <= prevTo)
                .GroupBy(l => new { l.ProductId, l.Product.ProductName })
                .Select(g => new { g.Key.ProductId, g.Key.ProductName, qty = g.Sum(x => x.Quantity), amount = g.Sum(x => x.LineTotal) })
                .ToListAsync();

            var productRows = prevByProduct
                .Select(pv =>
                {
                    var now = curByProduct.FirstOrDefault(c => c.ProductId == pv.ProductId);
                    return new
                    {
                        id = pv.ProductId,
                        name = pv.ProductName,
                        wasQty = pv.qty,
                        nowQty = now?.qty ?? 0,
                        was = pv.amount,
                        now = now?.amount ?? 0m,
                        change = (now?.amount ?? 0m) - pv.amount
                    };
                })
                .Where(p => p.change < 0)
                .OrderBy(p => p.change)
                .Take(10)
                .ToList();

            // ── stock: things that used to sell and are now empty ──────────
            var soldBefore = prevByProduct.Select(p => p.ProductId).ToHashSet();
            var stockNow = await _db.Products.AsNoTracking()
                .Where(pr => soldBefore.Contains(pr.ProductId))
                .Select(pr => new
                {
                    id = pr.ProductId,
                    name = pr.ProductName,
                    minQty = pr.MinQty,
                    onHand = pr.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                })
                .ToListAsync();

            var stockOut = stockNow
                .Where(p => p.onHand <= 0)
                .Select(p => new
                {
                    p.id,
                    p.name,
                    p.onHand,
                    soldLastPeriod = prevByProduct.First(x => x.ProductId == p.id).amount
                })
                .OrderByDescending(p => p.soldLastPeriod)
                .Take(10)
                .ToList();

            // ── price and discount ─────────────────────────────────────────
            var curPricing = await cur
                .Select(i => new { i.Subtotal, i.DiscountAmount, i.TotalAmount })
                .ToListAsync();
            var prevPricing = await prev
                .Select(i => new { i.Subtotal, i.DiscountAmount, i.TotalAmount })
                .ToListAsync();

            var curSubtotal = curPricing.Sum(x => x.Subtotal);
            var prevSubtotal = prevPricing.Sum(x => x.Subtotal);
            var curDiscount = curPricing.Sum(x => x.DiscountAmount);
            var prevDiscount = prevPricing.Sum(x => x.DiscountAmount);

            // ── by rep ─────────────────────────────────────────────────────
            var curByRep = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey != "CANCELLED" && o.SalesPersonUserId != null
                         && o.OrderDate >= curFrom && o.OrderDate <= curTo)
                .GroupBy(o => new { o.SalesPersonUserId, o.SalesPersonUser!.User.FullName })
                .Select(g => new { id = g.Key.SalesPersonUserId, name = g.Key.FullName, amount = g.Sum(o => o.TotalAmount) })
                .ToListAsync();

            var prevByRep = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey != "CANCELLED" && o.SalesPersonUserId != null
                         && o.OrderDate >= prevFrom && o.OrderDate <= prevTo)
                .GroupBy(o => new { o.SalesPersonUserId, o.SalesPersonUser!.User.FullName })
                .Select(g => new { id = g.Key.SalesPersonUserId, name = g.Key.FullName, amount = g.Sum(o => o.TotalAmount) })
                .ToListAsync();

            var repRows = prevByRep
                .Select(pv => new
                {
                    pv.id,
                    pv.name,
                    was = pv.amount,
                    now = curByRep.FirstOrDefault(c => c.id == pv.id)?.amount ?? 0m
                })
                .Select(r => new { r.id, r.name, r.was, r.now, change = r.now - r.was })
                .Where(r => r.change < 0)
                .OrderBy(r => r.change)
                .ToList();

            // ── expenses that jumped ───────────────────────────────────────
            var curExpenses = await _db.Expenses.AsNoTracking()
                .Where(e => e.Status.StatusKey == "POSTED" && e.ExpenseDate >= curFrom && e.ExpenseDate <= curTo)
                .GroupBy(e => e.CategoryName)
                .Select(g => new { category = g.Key, amount = g.Sum(x => x.Amount) })
                .ToListAsync();

            var prevExpenses = await _db.Expenses.AsNoTracking()
                .Where(e => e.Status.StatusKey == "POSTED" && e.ExpenseDate >= prevFrom && e.ExpenseDate <= prevTo)
                .GroupBy(e => e.CategoryName)
                .Select(g => new { category = g.Key, amount = g.Sum(x => x.Amount) })
                .ToListAsync();

            var expenseRows = curExpenses
                .Select(c => new
                {
                    c.category,
                    now = c.amount,
                    was = prevExpenses.FirstOrDefault(p => p.category == c.category)?.amount ?? 0m
                })
                .Select(e => new { e.category, e.was, e.now, change = e.now - e.was })
                .Where(e => e.change > 0)
                .OrderByDescending(e => e.change)
                .Take(8)
                .ToList();

            return Ok(new
            {
                period = new { from = curFrom, to = curTo },
                comparedWith = new { from = prevFrom, to = prevTo },
                headline = new
                {
                    was = prevTotal,
                    now = curTotal,
                    change = curTotal - prevTotal,
                    changePercent = prevTotal > 0
                        ? Math.Round((curTotal - prevTotal) / prevTotal * 100, 1)
                        : (decimal?)null,
                    invoicesWas = prevCount,
                    invoicesNow = curCount
                },
                byCustomer = new
                {
                    lost = customerRows.Count(c => c.stoppedBuying),
                    lostValue = customerRows.Where(c => c.stoppedBuying).Sum(c => c.was),
                    items = customerRows
                },
                byProduct = new { items = productRows },
                stockOut = new
                {
                    count = stockOut.Count,
                    valueLastPeriod = stockOut.Sum(p => p.soldLastPeriod),
                    items = stockOut
                },
                pricing = new
                {
                    subtotalWas = prevSubtotal,
                    subtotalNow = curSubtotal,
                    discountWas = prevDiscount,
                    discountNow = curDiscount,
                    discountPercentWas = prevSubtotal > 0 ? Math.Round(prevDiscount / prevSubtotal * 100, 2) : 0m,
                    discountPercentNow = curSubtotal > 0 ? Math.Round(curDiscount / curSubtotal * 100, 2) : 0m,
                    averageInvoiceWas = prevCount > 0 ? Math.Round(prevTotal / prevCount, 0) : 0m,
                    averageInvoiceNow = curCount > 0 ? Math.Round(curTotal / curCount, 0) : 0m
                },
                byRep = new { items = repRows },
                expensesUp = new { items = expenseRows }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "work out why sales moved");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  AI: EXPLAINING NUMBERS THE DATABASE ALREADY WORKED OUT
    // ══════════════════════════════════════════════════════════════════

    /*  Every action in this section follows the same two steps:

          1. call the SQL endpoint above and get finished numbers
          2. hand those numbers to the model and ask it to put them in order
             and say them in a sentence

        The model is never asked a question it would have to compute an answer
        to, and it is never given database access. If it is not configured, or
        it fails, the endpoint still returns the numbers with `explanation:
        null` and the screen shows the figures without the commentary.        */

    /// <summary>
    /// The sales-drop breakdown, plus a few lines saying what it means.
    /// `explanation` is null when the model is unavailable -- the numbers are
    /// always there.
    /// </summary>
    [HttpGet("sales-drop/explain")]
    public async Task<IActionResult> ExplainSalesDrop(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] DateOnly? baseFrom, [FromQuery] DateOnly? baseTo,
        CancellationToken ct = default)
    {
        try
        {
            var action = await SalesDrop(from, to, baseFrom, baseTo);
            if (action is not OkObjectResult ok || ok.Value is null) return action;

            var facts = JsonSerializer.Serialize(ok.Value, ExportJson);

            var explanation = await _ai.ExplainAsync(
                "You are looking at one shop's sales for two periods, already worked out. " +
                "Say what changed and why, then what to do about it.\n\n" +
                "Answer in this shape:\n" +
                "- Two or three sentences naming the BIGGEST causes, largest first, with the " +
                "actual figures from the data.\n" +
                "- Then a line 'Ab yeh karein:' followed by exactly three short actions, each " +
                "naming a specific customer, product or number from the data.\n\n" +
                "If a customer stopped buying entirely, that is almost always the main story -- " +
                "say who. If stock ran out on something that used to sell, say which. " +
                "Do not blame anything the data does not show.",
                facts, ct);

            return Ok(new
            {
                data = ok.Value,
                explanation,
                aiAvailable = _ai.IsConfigured,
                /* The screen must label this. It is a reading of the numbers,
                   not another number. */
                disclaimer = "Written by AI from the figures above. Check it before acting on it."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "explain the sales movement");
        }
    }

    /// <summary>
    /// Who to chase for money, in the order most likely to actually pay --
    /// which is not the same as oldest-debt-first.
    /// </summary>
    [HttpGet("recovery-priority")]
    public async Task<IActionResult> RecoveryPriority(CancellationToken ct = default)
    {
        try
        {
            var today = Today();

            /* Step 1, SQL. Everything the ranking could possibly rest on:
               what is owed, how old it is, whether this customer has a habit of
               paying, and how close they are to their limit. */
            var raw = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "CANCELLED")
                .Select(i => new
                {
                    i.InvoiceId,
                    i.InvoiceNo,
                    i.CustomerUserId,
                    customer = i.CustomerUser.LegalName,
                    phone = i.CustomerUser.User.Phone,
                    creditLimit = i.CustomerUser.CreditLimit,
                    creditDays = i.CustomerUser.CreditDays,
                    rep = i.CustomerUser.SalesPersonUser != null
                        ? i.CustomerUser.SalesPersonUser.User.FullName : null,
                    i.DueDate,
                    i.InvoiceDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            var open = raw
                .Select(i => new { i, balance = i.total - i.paid })
                .Where(x => x.balance > 0.004m)
                .ToList();

            var byCustomer = open
                .GroupBy(x => new { x.i.CustomerUserId, x.i.customer, x.i.phone, x.i.creditLimit, x.i.rep })
                .Select(g =>
                {
                    var oldest = g.Min(x => x.i.DueDate);
                    var owed = g.Sum(x => x.balance);
                    var invoiced = g.Sum(x => x.i.total);
                    return new
                    {
                        id = g.Key.CustomerUserId,
                        name = g.Key.customer,
                        phone = g.Key.phone,
                        rep = g.Key.rep,
                        creditLimit = g.Key.creditLimit,
                        owed,
                        invoices = g.Count(),
                        oldestDueDate = oldest,
                        daysOverdue = Math.Max(0, today.DayNumber - oldest.DayNumber),
                        /* How much of what they were billed they have actually
                           paid. A customer who pays 90% slowly is a better call
                           than one who has paid nothing at all. */
                        settledRatio = invoiced > 0 ? Math.Round(g.Sum(x => x.i.paid) / invoiced * 100, 0) : 0m,
                        overLimit = g.Key.creditLimit > 0 && owed > g.Key.creditLimit
                    };
                })
                .OrderByDescending(c => c.owed)
                .Take(15)
                .ToList();

            var facts = JsonSerializer.Serialize(new
            {
                asOf = today,
                totalOutstanding = byCustomer.Sum(c => c.owed),
                customers = byCustomer
            }, ExportJson);

            // Step 2, AI. Ordering and wording only.
            var advice = await _ai.ExplainAsync(
                "This is who owes the shop money. Put them in the order they should be " +
                "telephoned TODAY.\n\n" +
                "Do not simply order by oldest or largest. Weigh how much is owed, how late it " +
                "is, whether they have paid most of their bills before (settledRatio), and " +
                "whether they are over their credit limit.\n\n" +
                "Answer as a numbered list, at most six entries. Each line: the name, the amount, " +
                "and a few words on why they come at that position. Then one short WhatsApp " +
                "message in Roman Urdu that would suit the first customer on the list.",
                facts, ct);

            return Ok(new
            {
                asOf = today,
                totalOutstanding = byCustomer.Sum(c => c.owed),
                customers = byCustomer,
                advice,
                aiAvailable = _ai.IsConfigured,
                disclaimer = "The order was suggested by AI from the figures above. Check it before acting on it."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "work out the recovery priority");
        }
    }

    /// <summary>
    /// Dead and slow-moving stock, with a line on what to do with each.
    /// </summary>
    [HttpGet("dead-stock/advice")]
    public async Task<IActionResult> DeadStockAdvice([FromQuery] int days = 90, CancellationToken ct = default)
    {
        try
        {
            if (days is < 7 or > 730) days = 90;
            var today = Today();
            var since = today.AddDays(-days);

            /* Sold quantity in the window, per product, alongside what is
               sitting on the shelf and what it cost. */
            var soldSince = await _db.SalesInvoiceItems.AsNoTracking()
                .Where(l => l.Invoice.Status.StatusKey != "CANCELLED" && l.Invoice.InvoiceDate >= since)
                .GroupBy(l => l.ProductId)
                .Select(g => new { productId = g.Key, qty = g.Sum(x => x.Quantity), lastSold = g.Max(x => x.Invoice.InvoiceDate) })
                .ToListAsync();

            var soldMap = soldSince.ToDictionary(x => x.productId);

            var products = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    brand = p.Brand.BrandName,
                    cost = p.CostPrice,
                    price = p.SalePrice,
                    onHand = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                })
                .ToListAsync();

            var dead = products
                .Where(p => p.onHand > 0)
                .Select(p => new
                {
                    p.id, p.sku, p.name, p.brand, p.cost, p.price, p.onHand,
                    soldInWindow = soldMap.TryGetValue(p.id, out var srec) ? srec.qty : 0,
                    lastSold = soldMap.TryGetValue(p.id, out var lrec) ? lrec.lastSold : (DateOnly?)null,
                    cashTiedUp = p.onHand * p.cost,
                    marginPercent = p.price > 0 ? Math.Round((p.price - p.cost) / p.price * 100, 1) : 0m
                })
                .Where(p => p.soldInWindow == 0)
                .OrderByDescending(p => p.cashTiedUp)
                .Take(20)
                .ToList();

            var facts = JsonSerializer.Serialize(new
            {
                windowDays = days,
                asOf = today,
                deadCount = dead.Count,
                totalCashTiedUp = dead.Sum(p => p.cashTiedUp),
                items = dead
            }, ExportJson);

            var advice = await _ai.ExplainAsync(
                $"These products have not sold at all in {days} days but stock is still sitting " +
                "on the shelf, with money in it.\n\n" +
                "For each of the top items give ONE line: the product, how much cash is stuck in " +
                "it, and what to do -- a specific discount percentage, what to bundle it with, " +
                "or send it back to the supplier. Use the margin figure to decide how much " +
                "discount is even possible without a loss.\n\n" +
                "Finish with one sentence giving the total amount of cash that would be freed.",
                facts, ct);

            return Ok(new
            {
                windowDays = days,
                asOf = today,
                deadCount = dead.Count,
                totalCashTiedUp = dead.Sum(p => p.cashTiedUp),
                items = dead,
                advice,
                aiAvailable = _ai.IsConfigured,
                disclaimer = "Suggestions written by AI from the figures above. Check them before acting."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "advise on the dead stock");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  ROLE DASHBOARDS
    // ══════════════════════════════════════════════════════════════════

    /*  Three of the four dashboards used to be built entirely from mock files
        in the frontend -- accountant, order-dept and sales. They are the first
        screen each of those roles sees after signing in, and every number on
        them belonged to nobody.

        One endpoint each. They deliberately return SMALL, FLAT payloads: the
        dashboard shows counts and totals, and a screen that has to pull three
        thousand rows to display the number 12 is the slowest screen in the
        app.                                                                  */

    /// <summary>
    /// The accountant's morning: what is waiting on them, what is owed, and
    /// what is in the drawer.
    /// </summary>
    [HttpGet("dashboard/accountant")]
    public async Task<IActionResult> AccountantDashboard()
    {
        try
        {
            var today = Today();
            var monthStart = new DateOnly(today.Year, today.Month, 1);

            /* Cash and bank, from the ledger rather than from a guess. Opening
               balance plus posted movement, exactly the way the trial balance
               computes it -- otherwise the dashboard and the statements would
               disagree about how much money there is. */
            var cashAccounts = await _db.Accounts.AsNoTracking()
                .Where(a => !a.IsGroup && a.AccountType.TypeName == "Cash & Bank")
                .Select(a => new
                {
                    id = a.AccountId,
                    name = a.AccountName,
                    code = a.AccountCode,
                    opening = a.OpeningBalance,
                    debit = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == "POSTED")
                        .Sum(l => (decimal?)l.DebitAmount) ?? 0m,
                    credit = a.JournalEntryLines
                        .Where(l => l.Entry.Status.StatusKey == "POSTED")
                        .Sum(l => (decimal?)l.CreditAmount) ?? 0m
                })
                .ToListAsync();

            var cashBreakdown = cashAccounts
                .Select(a => new { a.id, a.name, a.code, balance = a.opening + a.debit - a.credit })
                .OrderByDescending(a => a.balance)
                .ToList();

            /* Collections waiting on this accountant to confirm. */
            var awaiting = await _db.Collections.AsNoTracking()
                .Where(c => c.Status.StatusKey == "AWAITING")
                .Select(c => new
                {
                    id = c.CollectionId,
                    receiptNo = c.ReceiptNo,
                    customerName = c.CustomerUser.LegalName,
                    collectedBy = c.CollectedByUser.User.FullName,
                    collectedOn = c.CollectedOn,
                    amount = c.Amount,
                    method = c.Method.MethodName,
                    reference = c.ReferenceNo
                })
                .OrderBy(c => c.collectedOn)
                .Take(8)
                .ToListAsync();

            var awaitingCount = await _db.Collections.CountAsync(c => c.Status.StatusKey == "AWAITING");
            var awaitingTotal = await _db.Collections
                .Where(c => c.Status.StatusKey == "AWAITING")
                .SumAsync(c => (decimal?)c.Amount) ?? 0m;

            var confirmedToday = await _db.Collections
                .Where(c => c.Status.StatusKey == "CONFIRMED" && c.ConfirmedOn == today)
                .Select(c => c.Amount)
                .ToListAsync();

            /* Money in and out this month, posted vouchers only. */
            var receiptsMonth = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && v.VoucherType.IsReceipt && v.VoucherDate >= monthStart)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var paymentsMonth = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && !v.VoucherType.IsReceipt && v.VoucherDate >= monthStart)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var receiptsToday = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && v.VoucherType.IsReceipt && v.VoucherDate == today)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var paymentsToday = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && !v.VoucherType.IsReceipt && v.VoucherDate == today)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;

            /* Last month as well. On the 1st of a month "this month" is
               legitimately zero, and a card that only ever shows zero on the
               day people most want to look at it is a card nobody trusts. */
            var prevMonthStart = monthStart.AddMonths(-1);
            var receiptsPrevMonth = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && v.VoucherType.IsReceipt
                         && v.VoucherDate >= prevMonthStart && v.VoucherDate < monthStart)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;
            var paymentsPrevMonth = await _db.Vouchers
                .Where(v => v.Status.StatusKey == "POSTED" && !v.VoucherType.IsReceipt
                         && v.VoucherDate >= prevMonthStart && v.VoucherDate < monthStart)
                .SumAsync(v => (decimal?)v.Amount) ?? 0m;

            /* Payables: the supplier bill total less whatever posted vouchers
               have been allocated to it. Neither invoice table carries a
               "paid" column, so this is the only honest way to compute it. */
            var payablesRaw = await _db.PurchaseInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "CANCELLED")
                .Select(i => new
                {
                    id = i.PiId,
                    invoiceNo = i.InvoiceNo,
                    supplier = i.SupplierUser.LegalName,
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            var payables = payablesRaw
                .Select(i => new { i.id, i.invoiceNo, i.supplier, i.dueDate, i.total, balance = i.total - i.paid })
                .Where(i => i.balance > 0.004m)
                .OrderBy(i => i.dueDate)
                .ToList();

            var dueSoon = payables.Where(i => i.dueDate <= today.AddDays(3)).ToList();

            /* Receivables, in the aging buckets the recovery report uses. */
            var receivablesRaw = await _db.SalesInvoices.AsNoTracking()
                .Where(i => i.Status.StatusKey != "CANCELLED")
                .Select(i => new
                {
                    dueDate = i.DueDate,
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            var openReceivables = receivablesRaw
                .Select(i => new { i.dueDate, balance = i.total - i.paid })
                .Where(i => i.balance > 0.004m)
                .ToList();

            decimal Bucket(int fromDays, int? toDays) => openReceivables
                .Where(r =>
                {
                    var age = today.DayNumber - r.dueDate.DayNumber;
                    return age >= fromDays && (toDays is null || age <= toDays);
                })
                .Sum(r => r.balance);

            var draftExpenses = await _db.Expenses.CountAsync(e => e.Status.StatusKey == "DRAFT");
            var draftExpenseValue = await _db.Expenses
                .Where(e => e.Status.StatusKey == "DRAFT")
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;

            return Ok(new
            {
                asOf = today,
                cash = new
                {
                    total = cashBreakdown.Sum(a => a.balance),
                    breakdown = cashBreakdown
                },
                collections = new
                {
                    awaitingCount,
                    awaitingTotal,
                    confirmedTodayCount = confirmedToday.Count,
                    confirmedTodayTotal = confirmedToday.Sum(),
                    queue = awaiting
                },
                money = new { receiptsToday, paymentsToday, receiptsMonth, paymentsMonth, receiptsPrevMonth, paymentsPrevMonth },
                payables = new
                {
                    openCount = payables.Count,
                    openTotal = payables.Sum(p => p.balance),
                    dueSoonCount = dueSoon.Count,
                    dueSoonTotal = dueSoon.Sum(p => p.balance),
                    dueSoon = dueSoon.Take(6)
                },
                receivables = new
                {
                    total = openReceivables.Sum(r => r.balance),
                    current = Bucket(int.MinValue, 0),
                    days1To30 = Bucket(1, 30),
                    days31To60 = Bucket(31, 60),
                    days60Plus = Bucket(61, null)
                },
                expenses = new { draftCount = draftExpenses, draftValue = draftExpenseValue }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the accountant dashboard");
        }
    }

    /// <summary>
    /// The order department's board: what is queued, what is short, what is
    /// stuck with a supplier.
    /// </summary>
    [HttpGet("dashboard/order-dept")]
    public async Task<IActionResult> OrderDeptDashboard()
    {
        try
        {
            var today = Today();

            var byStatus = await _db.SalesOrders.AsNoTracking()
                .GroupBy(o => o.Status.StatusKey)
                .Select(g => new { status = g.Key, count = g.Count(), value = g.Sum(o => o.TotalAmount) })
                .ToListAsync();

            int CountOf(string key) => byStatus.FirstOrDefault(s => s.status == key)?.count ?? 0;
            decimal ValueOf(string key) => byStatus.FirstOrDefault(s => s.status == key)?.value ?? 0m;

            /* The oldest waiting orders, because those are the ones somebody
               is already on the phone about. */
            var queue = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey == "SUBMITTED" || o.Status.StatusKey == "CONFIRMED"
                         || o.Status.StatusKey == "PROCESSING")
                .OrderBy(o => o.OrderDate).ThenBy(o => o.OrderId)
                .Take(8)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customer = o.CustomerUser.LegalName,
                    orderDate = o.OrderDate,
                    deliveryDate = o.DeliveryDate,
                    total = o.TotalAmount,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    itemCount = o.SalesOrderItems.Count
                })
                .ToListAsync();

            /* Low stock: total on hand across every location against the
               product's own minimum. Summing first and comparing after is
               deliberate -- a product with 2 in the shop and 200 in the
               warehouse is not short. */
            var stockLevels = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive && p.MinQty > 0)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    minQty = p.MinQty,
                    onHand = p.StockBalances.Sum(b => (int?)b.Quantity) ?? 0
                })
                .ToListAsync();

            var lowStock = stockLevels
                .Where(p => p.onHand < p.minQty)
                .OrderBy(p => p.onHand)
                .ToList();

            var openClaims = await _db.Claims.AsNoTracking()
                .Where(c => c.Stage.IsOpen)
                .Select(c => new
                {
                    id = c.ClaimId,
                    claimNo = c.ClaimNo,
                    customer = c.CustomerUser.LegalName,
                    product = c.Product.ProductName,
                    quantity = c.Quantity,
                    value = c.Quantity * c.UnitCost,
                    stage = c.Stage.StageName,
                    receivedOn = c.ReceivedOn,
                    remindersSent = c.RemindersSent
                })
                .OrderBy(c => c.receivedOn)
                .ToListAsync();

            var dispatchedToday = await _db.Deliveries
                .CountAsync(d => d.BookedDate == today);
            var awaitingDispatch = await _db.Deliveries
                .CountAsync(d => d.Status.StatusKey == "NOT_DISPATCHED");
            var inTransit = await _db.Deliveries
                .CountAsync(d => d.Status.StatusKey == "IN_TRANSIT" || d.Status.StatusKey == "OUT_FOR_DELIVERY"
                              || d.Status.StatusKey == "AWAITING" || d.Status.StatusKey == "BOOKED");

            /* Transfers still on the road, which is stock nobody can sell. */
            var transfersInTransit = await _db.StockTransfers
                .CountAsync(t => t.Status.StatusKey != "RECEIVED" && t.Status.StatusKey != "CANCELLED");

            return Ok(new
            {
                asOf = today,
                orders = new
                {
                    submitted = CountOf("SUBMITTED"),
                    submittedValue = ValueOf("SUBMITTED"),
                    confirmed = CountOf("CONFIRMED"),
                    processing = CountOf("PROCESSING"),
                    packed = CountOf("PACKED"),
                    creditHold = CountOf("CREDIT_HOLD"),
                    creditHoldValue = ValueOf("CREDIT_HOLD"),
                    queue
                },
                packing = new { waiting = CountOf("CONFIRMED") + CountOf("PROCESSING"), packed = CountOf("PACKED") },
                dispatch = new { dispatchedToday, awaitingDispatch, inTransit, transfersInTransit },
                stock = new
                {
                    lowCount = lowStock.Count,
                    items = lowStock.Take(8)
                },
                claims = new
                {
                    openCount = openClaims.Count,
                    openValue = openClaims.Sum(c => c.value),
                    items = openClaims.Take(6)
                }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the order department dashboard");
        }
    }

    /// <summary>
    /// One rep's own numbers. Scoped to the signed-in user throughout -- a
    /// dashboard that showed a rep somebody else's orders would be worse than
    /// showing nothing.
    /// </summary>
    [HttpGet("dashboard/sales")]
    public async Task<IActionResult> SalesDashboard()
    {
        try
        {
            var me = CurrentUserId();
            var today = Today();
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var prevStart = monthStart.AddMonths(-1);

            var mine = _db.SalesOrders.AsNoTracking().Where(o => o.SalesPersonUserId == me);

            var monthOrders = await mine
                .Where(o => o.OrderDate >= monthStart && o.Status.StatusKey != "CANCELLED")
                .Select(o => o.TotalAmount)
                .ToListAsync();

            var prevMonthTotal = await mine
                .Where(o => o.OrderDate >= prevStart && o.OrderDate < monthStart && o.Status.StatusKey != "CANCELLED")
                .SumAsync(o => (decimal?)o.TotalAmount) ?? 0m;

            var recent = await mine
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.OrderId)
                .Take(8)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customer = o.CustomerUser.LegalName,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName
                })
                .ToListAsync();

            var onHold = await mine
                .Where(o => o.Status.StatusKey == "CREDIT_HOLD")
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customer = o.CustomerUser.LegalName,
                    total = o.TotalAmount,
                    reason = o.CreditHoldReason
                })
                .ToListAsync();

            /* Collections this rep took that the accountant has not confirmed
               yet -- money they have physically handled and are answerable for. */
            var pendingCollections = await _db.Collections.AsNoTracking()
                .Where(c => c.CollectedByUserId == me && c.Status.StatusKey == "AWAITING")
                .Select(c => new
                {
                    id = c.CollectionId,
                    receiptNo = c.ReceiptNo,
                    customer = c.CustomerUser.LegalName,
                    amount = c.Amount,
                    collectedOn = c.CollectedOn
                })
                .OrderBy(c => c.collectedOn)
                .ToListAsync();

            var visitsToday = await _db.CustomerVisits
                .CountAsync(v => v.SalesPersonUserId == me
                              && v.VisitedAt >= today.ToDateTime(TimeOnly.MinValue)
                              && v.VisitedAt < today.AddDays(1).ToDateTime(TimeOnly.MinValue));

            var visitsMonth = await _db.CustomerVisits
                .CountAsync(v => v.SalesPersonUserId == me
                              && v.VisitedAt >= monthStart.ToDateTime(TimeOnly.MinValue));

            /* What this rep's own customers still owe.

               "Mine" is deliberately BOTH: customers formally assigned to this
               rep, and customers they have actually taken an order for. The
               assignment alone is too narrow -- a rep who covers for a
               colleague is answerable for that money too, and in this database
               plenty of orders are taken by someone the customer is not
               assigned to. Neither list alone matches what the rep thinks of
               as their book. */
            var assignedIds = await _db.Parties.AsNoTracking()
                .Where(pa => pa.SalesPersonUserId == me)
                .Select(pa => pa.UserId)
                .ToListAsync();

            var soldToIds = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.SalesPersonUserId == me)
                .Select(o => o.CustomerUserId)
                .Distinct()
                .ToListAsync();

            var myCustomerIds = assignedIds.Union(soldToIds).ToList();

            var owedRaw = await _db.SalesInvoices.AsNoTracking()
                .Where(i => myCustomerIds.Contains(i.CustomerUserId) && i.Status.StatusKey != "CANCELLED")
                .Select(i => new
                {
                    total = i.TotalAmount,
                    paid = i.VoucherAllocations
                        .Where(a => a.Voucher.Status.StatusKey == "POSTED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            var outstanding = owedRaw.Sum(i => i.total - i.paid);

            return Ok(new
            {
                asOf = today,
                orders = new
                {
                    monthCount = monthOrders.Count,
                    monthValue = monthOrders.Sum(),
                    prevMonthValue = prevMonthTotal,
                    recent
                },
                creditHolds = new { count = onHold.Count, value = onHold.Sum(o => o.total), items = onHold },
                collections = new
                {
                    pendingCount = pendingCollections.Count,
                    pendingTotal = pendingCollections.Sum(c => c.amount),
                    items = pendingCollections.Take(6)
                },
                customers = new { count = myCustomerIds.Count, outstanding },
                visits = new { today = visitsToday, month = visitsMonth }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the sales dashboard");
        }
    }

    [HttpGet("{key}/pdf")]
    public async Task<IActionResult> RenderPdf(string key,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? locationId,
        [FromQuery] DateOnly? asOf, [FromQuery] int days = 90,
        [FromQuery] int minCoverDays = 120, [FromQuery] int limit = 20)
    {
        try
        {
            var built = await BuildReport(key, from, to, locationId, asOf, days, minCoverDays, limit);
            if (built.Error is not null) return built.Error;

            Response.Headers.ContentDisposition = $"inline; filename=\"{built.FileName}\"";
            return File(DocumentPdf.Render(built.Doc!), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the {key.Replace('-', ' ')} report");
        }
    }

    /// <summary>
    /// Renders the report and pushes it to the documents Cloudinary account.
    /// Re-running the same report with the same parameters replaces its file.
    /// </summary>
    [HttpPost("{key}/pdf")]
    public async Task<IActionResult> ArchivePdf(string key,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int? locationId,
        [FromQuery] DateOnly? asOf, [FromQuery] int days = 90,
        [FromQuery] int minCoverDays = 120, [FromQuery] int limit = 20)
    {
        try
        {
            var built = await BuildReport(key, from, to, locationId, asOf, days, minCoverDays, limit);
            if (built.Error is not null) return built.Error;

            var kind = $"report.{key}";
            var stored = await DocumentArchive.StoreAsync(_db, _cfg, kind, built.Fingerprint!,
                built.Doc!.Title, built.FileName!, DocumentPdf.Render(built.Doc!),
                CurrentUserId(), "reports");

            await Log("REPORT_ARCHIVED", kind, built.Doc!.Title, stored.PdfUrl, 1);

            return Ok(new
            {
                archived = true,
                fileId = stored.FileId,
                kind,
                fileName = stored.FileName,
                pdfUrl = stored.PdfUrl,
                bytes = stored.Bytes,
                isDeliverable = stored.Deliverable,
                generatedAt = stored.GeneratedAt,
                message = stored.Deliverable
                    ? $"{built.Doc!.Title} saved to the document store."
                    : $"{built.Doc!.Title} saved. The store will not serve PDFs yet -- see the Cloudinary setting."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the {key.Replace('-', ' ')} report");
        }
    }

    private sealed record BuiltReport(
        DocumentPdf.Data? Doc, string? FileName, string? Fingerprint, IActionResult? Error);

    /// <summary>
    /// Runs the report's own action, then shapes its result for the renderer.
    /// The JsonElement hop is deliberate: it is the exact payload the browser
    /// receives, so the paper and the screen cannot disagree.
    /// </summary>
    private async Task<BuiltReport> BuildReport(string key,
        DateOnly? from, DateOnly? to, int? locationId, DateOnly? asOf,
        int days, int minCoverDays, int limit)
    {
        if (!ReportKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new BuiltReport(null, null, null,
                NotFound(new { message = $"'{key}' is not a report. Try: {string.Join(", ", ReportKeys)}." }));

        IActionResult action = key.ToLowerInvariant() switch
        {
            "sales-summary" => await SalesSummary(from, to, locationId),
            "aging-customer" => await CustomerAging(asOf),
            "aging-supplier" => await SupplierAging(asOf),
            "dead-stock" => await DeadStock(days),
            "slow-moving" => await SlowMoving(days, minCoverDays),
            _ => await TopCustomers(from, to, limit)
        };

        if (action is not OkObjectResult ok || ok.Value is null)
            return new BuiltReport(null, null, null, action);

        var j = JsonSerializer.SerializeToElement(ok.Value, JsonShape);
        var company = await LetterHead();
        var cur = company.CurrencySymbol;

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        return key.ToLowerInvariant() switch
        {
            "sales-summary" => Wrap(SalesSummaryPdf(j, company, cur),
                $"sales-summary-{Str(j, "from")}-to-{Str(j, "to")}.pdf",
                $"{Str(j, "from")}:{Str(j, "to")}:{locationId?.ToString() ?? "all"}"),

            "aging-customer" => Wrap(AgingPdf(j, company, cur, customers: true),
                $"customer-ageing-{Str(j, "asOf")}.pdf", $"{Str(j, "asOf")}"),

            "aging-supplier" => Wrap(AgingPdf(j, company, cur, customers: false),
                $"supplier-ageing-{Str(j, "asOf")}.pdf", $"{Str(j, "asOf")}"),

            "dead-stock" => Wrap(DeadStockPdf(j, company, cur),
                $"dead-stock-{days}d-{stamp}.pdf", $"{days}"),

            "slow-moving" => Wrap(SlowMovingPdf(j, company, cur),
                $"slow-moving-{days}d-{minCoverDays}c-{stamp}.pdf", $"{days}:{minCoverDays}"),

            _ => Wrap(TopCustomersPdf(j, company, cur),
                $"top-customers-{Str(j, "from")}-to-{Str(j, "to")}.pdf",
                $"{Str(j, "from")}:{Str(j, "to")}:{limit}")
        };

        static BuiltReport Wrap(DocumentPdf.Data doc, string file, string fingerprint) =>
            new(doc, file, fingerprint, null);
    }

    /* ─────────────────────── the six report layouts ─────────────────────── */

    private DocumentPdf.Data SalesSummaryPdf(JsonElement j, DocumentPdf.LetterHead c, string cur) =>
        new(
            Company: c,
            Title: "Sales Summary",
            DocNo: null,
            StatusName: null,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", Day(j, "from")),
                new DocumentPdf.Fact("To", Day(j, "to")),
                new DocumentPdf.Fact("Invoices", Num(j, "invoiceCount")),
                new DocumentPdf.Fact("Units Sold", Num(j, "unitsSold")),
                new DocumentPdf.Fact("Margin", $"{Dec(j, "marginPercent"):0.#}%"),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Date", 2.4),
                new DocumentPdf.Col("Invoices", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Units", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Revenue", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Cost", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Margin", 2, DocumentPdf.Align.Right),
            },
            Rows: Arr(j, "byDay").Select(d => new DocumentPdf.Row(new[]
            {
                Day(d, "date"), Num(d, "invoices"), Num(d, "units"),
                DocumentPdf.Money(Dec(d, "revenue")),
                DocumentPdf.Money(Dec(d, "cost")),
                DocumentPdf.Money(Dec(d, "margin"))
            })).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Subtotal", DocumentPdf.Money(Dec(j, "subtotal"), cur)),
                new DocumentPdf.Total("Discount", DocumentPdf.Money(-Dec(j, "discount"), cur), Colour: DocumentPdf.Danger),
                new DocumentPdf.Total("Tax", DocumentPdf.Money(Dec(j, "tax"), cur)),
                new DocumentPdf.Total("Cost of sales", DocumentPdf.Money(Dec(j, "cost"), cur)),
                new DocumentPdf.Total("Gross margin", DocumentPdf.Money(Dec(j, "margin"), cur), Colour: DocumentPdf.Success),
                new DocumentPdf.Total("Average invoice", DocumentPdf.Money(Dec(j, "averageInvoice"), cur)),
                new DocumentPdf.Total("Revenue", DocumentPdf.Money(Dec(j, "revenue"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Revenue is invoiced value. Cost is the unit cost captured on each line at invoice time.",
            PreparedBy: null,
            EmptyMessage: "No invoices in this period.",
            More: new[]
            {
                new DocumentPdf.Section("By location",
                    new[]
                    {
                        new DocumentPdf.Col("Location", 3.6),
                        new DocumentPdf.Col("Invoices", 1.4, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Revenue", 2.2, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Cost", 2.2, DocumentPdf.Align.Right),
                        new DocumentPdf.Col("Margin", 2.2, DocumentPdf.Align.Right),
                    },
                    Arr(j, "byLocation").Select(l => new DocumentPdf.Row(new[]
                    {
                        Str(l, "location"), Num(l, "invoices"),
                        DocumentPdf.Money(Dec(l, "revenue")),
                        DocumentPdf.Money(Dec(l, "cost")),
                        DocumentPdf.Money(Dec(l, "margin"))
                    })).ToList())
            });

    private DocumentPdf.Data AgingPdf(JsonElement j, DocumentPdf.LetterHead c, string cur, bool customers)
    {
        var nameField = customers ? "customerName" : "supplierName";
        var countField = customers ? "customerCount" : "supplierCount";

        return new DocumentPdf.Data(
            Company: c,
            Title: customers ? "Customer Ageing" : "Supplier Ageing",
            DocNo: null,
            StatusName: null,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("As At", Day(j, "asOf")),
                new DocumentPdf.Fact(customers ? "Customers" : "Suppliers", Num(j, countField)),
                new DocumentPdf.Fact("Outstanding", DocumentPdf.Money(Dec(j, "totalOutstanding"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col(customers ? "Customer" : "Supplier", 3.4),
                new DocumentPdf.Col("Inv", 0.8, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Current", 1.6, DocumentPdf.Align.Right),
                new DocumentPdf.Col("1-30", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("31-60", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("61-90", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("90+", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Outstanding", 1.9, DocumentPdf.Align.Right),
            },
            Rows: Arr(j, "items").Select(r => new DocumentPdf.Row(new[]
            {
                Str(r, nameField), Num(r, "invoiceCount"),
                Zero(Dec(r, "current")), Zero(Dec(r, "d0_30")), Zero(Dec(r, "d31_60")),
                Zero(Dec(r, "d61_90")), Zero(Dec(r, "d90plus")),
                DocumentPdf.Money(Dec(r, "outstanding"))
            }, Sub: customers && Bool(r, "overLimit") ? "over credit limit" : null)).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Current", DocumentPdf.Money(Dec(j, "current"), cur)),
                new DocumentPdf.Total("1-30 days", DocumentPdf.Money(Dec(j, "d0_30"), cur)),
                new DocumentPdf.Total("31-60 days", DocumentPdf.Money(Dec(j, "d31_60"), cur)),
                new DocumentPdf.Total("61-90 days", DocumentPdf.Money(Dec(j, "d61_90"), cur)),
                new DocumentPdf.Total("Over 90 days", DocumentPdf.Money(Dec(j, "d90plus"), cur), Colour: DocumentPdf.Danger),
                new DocumentPdf.Total("Total Outstanding", DocumentPdf.Money(Dec(j, "totalOutstanding"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Aged from the DUE date, not the invoice date -- an invoice on 60-day terms is not overdue on day 31.",
            PreparedBy: null,
            EmptyMessage: "Nothing outstanding as at this date.");
    }

    private DocumentPdf.Data DeadStockPdf(JsonElement j, DocumentPdf.LetterHead c, string cur) =>
        new(
            Company: c,
            Title: "Dead Stock",
            DocNo: null,
            StatusName: null,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("Window", $"{Num(j, "windowDays")} days"),
                new DocumentPdf.Fact("Items", Num(j, "count")),
                new DocumentPdf.Fact("Tied Up", DocumentPdf.Money(Dec(j, "tiedUpValue"))),
                new DocumentPdf.Fact("As At", DocumentPdf.Day(Today())),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Item", 4.4),
                new DocumentPdf.Col("Brand", 1.8),
                new DocumentPdf.Col("On Hand", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Cost", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Last Sold", 1.7, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Tied Up", 1.8, DocumentPdf.Align.Right),
            },
            Rows: Arr(j, "items").Select(r => new DocumentPdf.Row(new[]
            {
                Str(r, "name"), Str(r, "brand"), Num(r, "onHand"),
                DocumentPdf.Money(Dec(r, "costPrice")),
                NullableInt(r, "daysSinceLastOut") is int d ? $"{d} days ago" : "never",
                DocumentPdf.Money(Dec(r, "tiedUpValue"))
            }, Sub: $"{Str(r, "sku")}  ·  {Str(r, "category")}")).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Items not moving", Num(j, "count")),
                new DocumentPdf.Total("Capital Tied Up", DocumentPdf.Money(Dec(j, "tiedUpValue"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Items with stock on hand and no outward movement in the window. Zero-stock items are excluded.",
            PreparedBy: null,
            EmptyMessage: "Nothing has been sitting still this long.");

    private DocumentPdf.Data SlowMovingPdf(JsonElement j, DocumentPdf.LetterHead c, string cur) =>
        new(
            Company: c,
            Title: "Slow Moving Stock",
            DocNo: null,
            StatusName: null,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("Window", $"{Num(j, "windowDays")} days"),
                new DocumentPdf.Fact("Cover Over", $"{Num(j, "minCoverDays")} days"),
                new DocumentPdf.Fact("Items", Num(j, "count")),
                new DocumentPdf.Fact("Tied Up", DocumentPdf.Money(Dec(j, "tiedUpValue"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Item", 4.2),
                new DocumentPdf.Col("On Hand", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Sold", 1.2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Per Day", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Cover", 1.5, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Tied Up", 1.8, DocumentPdf.Align.Right),
            },
            Rows: Arr(j, "items").Select(r => new DocumentPdf.Row(new[]
            {
                Str(r, "name"), Num(r, "onHand"), Num(r, "soldInWindow"),
                $"{Dec(r, "perDay"):0.##}",
                $"{Num(r, "coverDays")} days",
                DocumentPdf.Money(Dec(r, "tiedUpValue"))
            }, Sub: $"{Str(r, "sku")}  ·  {Str(r, "brand")}")).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Slow movers", Num(j, "count")),
                new DocumentPdf.Total("Capital Tied Up", DocumentPdf.Money(Dec(j, "tiedUpValue"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Days of cover, not units sold, is the number that matters: 5 a month is fine on a 10-unit shelf and terrible on a 500-unit one.",
            PreparedBy: null,
            EmptyMessage: "Everything is turning over inside the cover threshold.");

    private DocumentPdf.Data TopCustomersPdf(JsonElement j, DocumentPdf.LetterHead c, string cur) =>
        new(
            Company: c,
            Title: "Top Customers",
            DocNo: null,
            StatusName: null,
            Counterparty: null,
            Meta: new[]
            {
                new DocumentPdf.Fact("From", Day(j, "from")),
                new DocumentPdf.Fact("To", Day(j, "to")),
                new DocumentPdf.Fact("Customers", Num(j, "count")),
                new DocumentPdf.Fact("Revenue", DocumentPdf.Money(Dec(j, "totalRevenue"))),
            },
            Columns: new[]
            {
                new DocumentPdf.Col("Customer", 3.6),
                new DocumentPdf.Col("Inv", 0.9, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Revenue", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Margin", 2, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Margin %", 1.3, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Avg Invoice", 1.8, DocumentPdf.Align.Right),
                new DocumentPdf.Col("Last Buy", 1.6, DocumentPdf.Align.Right),
            },
            Rows: Arr(j, "items").Select(r => new DocumentPdf.Row(new[]
            {
                Str(r, "customerName"), Num(r, "invoiceCount"),
                DocumentPdf.Money(Dec(r, "revenue")),
                DocumentPdf.Money(Dec(r, "margin")),
                $"{Dec(r, "marginPercent"):0.#}%",
                DocumentPdf.Money(Dec(r, "averageInvoice")),
                $"{Num(r, "daysSinceLastInvoice")}d ago"
            }, Sub: Str(r, "city"))).ToList(),
            Totals: new[]
            {
                new DocumentPdf.Total("Total margin", DocumentPdf.Money(Dec(j, "totalMargin"), cur), Colour: DocumentPdf.Success),
                new DocumentPdf.Total("Total Revenue", DocumentPdf.Money(Dec(j, "totalRevenue"), cur), Emphasis: true),
            },
            Notes: null,
            Footnote: "Ranked by invoiced revenue over the period. Margin uses the unit cost captured at invoice time.",
            PreparedBy: null,
            EmptyMessage: "No invoices in this period.");

    /* ───────────────────────── json + letterhead ───────────────────────── */

    /* The API serialises anonymous objects with their own property names, which
       are already camelCase. Matching that here means the readers below use the
       same keys the browser sees. */
    private static readonly JsonSerializerOptions JsonShape = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    private static IEnumerable<JsonElement> Arr(JsonElement j, string name) =>
        j.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static decimal Dec(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static string Num(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64().ToString("N0", CultureInfo.GetCultureInfo("en-US"))
            : "0";

    private static bool Bool(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? NullableInt(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>A date the API sent as "2026-08-29", shown as "29 Aug 2026".</summary>
    private static string Day(JsonElement j, string name) =>
        DateOnly.TryParse(Str(j, name), CultureInfo.InvariantCulture, out var d)
            ? DocumentPdf.Day(d)
            : Str(j, name);

    /// <summary>An ageing bucket: a dash reads better than a column of 0.00.</summary>
    private static string Zero(decimal v) => v == 0 ? "-" : DocumentPdf.Money(v);

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

}
