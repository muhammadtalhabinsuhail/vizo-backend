using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

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
    public ReportsController(AppDbContext db, IConfiguration cfg,
        ILogger<ReportsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

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
