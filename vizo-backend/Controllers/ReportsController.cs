using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
}
