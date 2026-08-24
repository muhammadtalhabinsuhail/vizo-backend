using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// The owner overview and the two decisions only the owner may take: letting a
/// credit-held order through, and putting one back on hold.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match exactly what the screen
/// renders.
///
/// Every action is wrapped in try/catch and reports through Fail(), so a failure
/// reaches the browser as JSON with the real exception message instead of an
/// empty 500. See AdminControllerBase.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = "SuperAdmin")]
public class AdminDashboardController : AdminControllerBase
{
    public AdminDashboardController(AppDbContext db, IConfiguration cfg, ILogger<AdminDashboardController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  DASHBOARD
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        try
        {
            /* "Today" means today. When the database holds historical data and
               today is quiet, fall back to the most recent day that actually has
               invoices and hand the date back, so the screen can label what it is
               showing instead of just printing a zero. */
            var businessDate = await _db.SalesInvoices
                .Where(i => i.InvoiceDate <= Today())
                .OrderByDescending(i => i.InvoiceDate)
                .Select(i => (DateOnly?)i.InvoiceDate)
                .FirstOrDefaultAsync() ?? Today();

            var daySales = await _db.SalesInvoices
                .Where(i => i.InvoiceDate == businessDate)
                .SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

            var dayOrders = await _db.SalesOrders.CountAsync(o => o.OrderDate == businessDate);

            var collectedToday = await _db.Collections
                .Where(c => c.ConfirmedOn == businessDate && c.StatusId == 2)
                .SumAsync(c => (decimal?)c.Amount) ?? 0m;

            /* Receivable and payable come from the ledger, which is the single
               source of truth -- never from a stored balance column. */
            var arOutstanding = await _db.JournalEntryLines
                .Where(l => l.AccountId == 10 && l.Entry.StatusId == 2)
                .SumAsync(l => (decimal?)(l.DebitAmount - l.CreditAmount)) ?? 0m;
            var arOpening = await _db.Accounts.Where(a => a.AccountId == 10)
                .Select(a => a.OpeningBalance).FirstOrDefaultAsync();
            arOutstanding += arOpening;

            var apPayable = await _db.JournalEntryLines
                .Where(l => l.AccountId == 19 && l.Entry.StatusId == 2)
                .SumAsync(l => (decimal?)(l.CreditAmount - l.DebitAmount)) ?? 0m;
            var apOpening = await _db.Accounts.Where(a => a.AccountId == 19)
                .Select(a => a.OpeningBalance).FirstOrDefaultAsync();
            apPayable += apOpening;

            var cutoff60 = businessDate.AddDays(-60);
            var overdue60 = await _db.SalesInvoices
                .Where(i => i.DueDate < cutoff60)
                .SumAsync(i => (decimal?)(i.TotalAmount -
                    i.VoucherAllocations.Sum(a => (decimal?)a.Amount) ?? 0m)) ?? 0m;

            var dueIn7 = await _db.PurchaseInvoices
                .Where(i => i.DueDate >= businessDate && i.DueDate <= businessDate.AddDays(7))
                .SumAsync(i => (decimal?)i.TotalAmount) ?? 0m;

            /* Orders sitting on the owner's approval queue. */
            var limitCrossed = await _db.SalesOrders
                .Where(o => o.Status.StatusKey == "CREDIT_HOLD")
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerName = o.CustomerUser.LegalName,
                    customerInitials = "",
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : "-",
                    total = o.TotalAmount,
                    creditHoldReason = o.CreditHoldReason,
                    creditLimit = o.CustomerUser.CreditLimit
                })
                .ToListAsync();

            var claims = await _db.Claims
                .Where(c => c.Stage.IsOpen)
                .Select(c => new { c.Quantity, c.UnitCost })
                .ToListAsync();
            var claimValue = claims.Sum(c => c.Quantity * c.UnitCost);

            var awaiting = await _db.Collections
                .Where(c => c.Status.StatusKey == "AWAITING")
                .Select(c => c.Amount)
                .ToListAsync();

            var deadStock = await _db.StockBalances
                .Where(s => s.Quantity > 0 && !s.Location.ExcludeFromSellable)
                .Where(s => !_db.SalesInvoiceItems.Any(ii => ii.ProductId == s.ProductId))
                .SumAsync(s => (decimal?)(s.Quantity * s.Product.CostPrice)) ?? 0m;

            var activity = await _db.ActivityLogs
                .OrderByDescending(a => a.LoggedAt)
                .Take(6)
                .Select(a => new
                {
                    id = a.LogId,
                    user = a.User != null ? a.User.FullName : "System",
                    action = a.ActionName,
                    target = a.EntityReference,
                    detail = a.Detail,
                    time = a.LoggedAt,
                    location = a.Location != null ? a.Location.LocationName : null,
                    severity = a.Severity.SeverityKey
                })
                .ToListAsync();

            /* Thirty days of invoiced revenue for the trend chart. */
            var from = businessDate.AddDays(-30);
            var trendRaw = await _db.SalesInvoices
                .Where(i => i.InvoiceDate >= from && i.InvoiceDate <= businessDate)
                .GroupBy(i => i.InvoiceDate)
                .Select(g => new { date = g.Key, revenue = g.Sum(x => x.TotalAmount) })
                .ToListAsync();
            var trend = trendRaw.OrderBy(t => t.date).ToList();

            return Ok(new
            {
                businessDate,
                todaySales = new { value = daySales, orders = dayOrders },
                collections = new { value = collectedToday },
                arOutstanding = new { value = arOutstanding, overdue60Plus = overdue60 },
                apPayable = new { value = apPayable, dueIn7Days = dueIn7 },
                limitCrossed = limitCrossed.Select(o => new
                {
                    o.id, o.orderNo, o.customerName,
                    customerInitials = Initials(o.customerName),
                    o.salesPerson, o.total, o.creditHoldReason, o.creditLimit
                }),
                claimsStuck = new { count = claims.Count, value = claimValue },
                deadStockValue = deadStock,
                awaitingCollections = new { count = awaiting.Count, value = awaiting.Sum() },
                activity,
                salesTrend = trend
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/dashboard");
        }
    }

    /// <summary>Owner lets an over-limit order through. Their risk, so it is
    /// recorded against their name.</summary>
    [HttpPost("orders/{id:int}/approve-credit-hold")]
    public async Task<IActionResult> ApproveCreditHold(int id, [FromBody] ReasonRequest? body)
    {
        try
        {
            var order = await _db.SalesOrders.Include(o => o.Status).Include(o => o.CustomerUser)
                .FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = "Order not found." });
            if (order.Status.StatusKey != "CREDIT_HOLD")
                return BadRequest(new { message = "That order is not waiting on a limit decision." });

            var confirmed = await _db.OrderStatuses.FirstAsync(s => s.StatusKey == "CONFIRMED");
            order.StatusId = confirmed.StatusId;
            order.CreditHoldReason = null;
            await _db.SaveChangesAsync();

            await Log("CREDIT_APPROVED", "SalesOrder", order.OrderNo,
                      body?.Reason ?? "Approved over the credit limit by the owner", 3);

            return Ok(new { message = $"{order.OrderNo} approved and sent to the order department." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/orders/{id:int}/approve-credit-hold");
        }
    }

    /// <summary>Owner keeps it held. The note goes back to the rep.</summary>
    [HttpPost("orders/{id:int}/hold")]
    public async Task<IActionResult> HoldOrder(int id, [FromBody] ReasonRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
                return BadRequest(new { message = "Give the rep a reason of at least 5 characters." });

            var order = await _db.SalesOrders.FirstOrDefaultAsync(o => o.OrderId == id);
            if (order is null) return NotFound(new { message = "Order not found." });

            order.CreditHoldReason = body.Reason.Trim();
            await _db.SaveChangesAsync();
            await Log("CREDIT_HELD", "SalesOrder", order.OrderNo, body.Reason.Trim(), 3);

            return Ok(new { message = $"{order.OrderNo} stays held." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/orders/{id:int}/hold");
        }
    }

    // ══════════════════════ request bodies ══════════════════════

    // ══════════════════════ request bodies ════════════════════════════

    public record ReasonRequest(string? Reason);
}