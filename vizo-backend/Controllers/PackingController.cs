using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /packing bench -- the Order Department's queue of orders to pick and box.
///
/// An order arrives here once it is CONFIRMED or PROCESSING and leaves as
/// PACKED, at which point it shows up on /dispatch. Orders sitting at
/// CREDIT_HOLD deliberately never appear: the owner has not released them yet
/// and packing stock against an unapproved order is exactly what the credit
/// control is there to prevent.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/packing")]
[ApiController]
[Authorize(Policy = "OrderDept")]
public class PackingController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public PackingController(AppDbContext db, IConfiguration cfg,
        ILogger<PackingController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    /* The two states that mean "this needs packing". */
    private static readonly string[] Queue = { "CONFIRMED", "PROCESSING" };

    // ══════════════════════════════════════════════════════════════════
    //  THE QUEUE
    // ══════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> GetPackingQueue([FromQuery] int? locationId, [FromQuery] string? q)
    {
        try
        {
            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => Queue.Contains(o.Status.StatusKey));

            if (locationId is not null) rows = rows.Where(o => o.LocationId == locationId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(o => o.OrderNo.ToLower().Contains(term) ||
                                       o.CustomerUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderBy(o => o.DeliveryDate ?? o.OrderDate).ThenBy(o => o.OrderId)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = o.CustomerUser.LegalName,
                    city = o.CustomerUser.City.CityName,
                    locationId = o.LocationId,
                    location = o.Location.LocationName,
                    orderDate = o.OrderDate,
                    deliveryDate = o.DeliveryDate,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    total = o.TotalAmount,
                    itemCount = o.SalesOrderItems.Count,
                    totalUnits = o.SalesOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : null,
                    lines = o.SalesOrderItems.OrderBy(i => i.LineNo).Select(i => new
                    {
                        productId = i.ProductId,
                        sku = i.Product.Sku,
                        name = i.Product.ProductName,
                        packing = i.Product.Packing,
                        qty = i.Quantity,

                        /* What is actually on the shelf at THIS order's location.
                           A picker needs to know before walking to the rack. */
                        onHand = i.Product.StockBalances
                            .Where(s => s.LocationId == o.LocationId)
                            .Sum(s => (int?)s.Quantity) ?? 0
                    }).ToList()
                })
                .ToListAsync();

            var today = Today();
            var shaped = items.Select(o => new
            {
                o.id, o.orderNo, o.customerId, o.customerName,
                customerInitials = Initials(o.customerName),
                o.city, o.locationId, o.location, o.orderDate, o.deliveryDate,
                o.status, o.statusName, o.total, o.itemCount, o.totalUnits,
                o.salesPerson, o.lines,
                waitingDays = today.DayNumber - o.orderDate.DayNumber,
                isLate = o.deliveryDate != null && o.deliveryDate < today,

                /* Can this order actually be packed right now? If any line is
                   short the bench needs to know before it starts, not halfway. */
                canPack = o.lines.All(l => l.onHand >= l.qty),
                shortLines = o.lines.Where(l => l.onHand < l.qty)
                    .Select(l => new { l.sku, l.name, l.qty, l.onHand, short_ = l.qty - l.onHand })
                    .ToList()
            }).ToList();

            return Ok(new
            {
                waiting = shaped.Count,
                late = shaped.Count(o => o.isLate),
                blocked = shaped.Count(o => !o.canPack),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the packing queue");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  PACKING AN ORDER
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Marks an order packed and takes the stock off the shelf.
    ///
    /// Stock leaves at PACKING, not at invoice: the goods have physically left
    /// the rack and the shelf count must say so. Every reduction writes a
    /// StockMovement row so the movement report can explain where it went.
    /// </summary>
    [HttpPost("{id:int}/pack")]
    public async Task<IActionResult> Pack(int id)
    {
        try
        {
            var order = await _db.SalesOrders
                .Include(o => o.Status)
                .Include(o => o.SalesOrderItems)
                .FirstOrDefaultAsync(o => o.OrderId == id);

            if (order is null) return NotFound(new { message = $"No order with id {id}." });

            if (order.Status.StatusKey == "CREDIT_HOLD")
                return BadRequest(new
                {
                    message = $"{order.OrderNo} is on credit hold and needs the owner's approval before it can be packed."
                });
            if (order.Status.StatusKey == "PACKED")
                return BadRequest(new { message = $"{order.OrderNo} is already packed." });
            if (!Queue.Contains(order.Status.StatusKey))
                return BadRequest(new
                {
                    message = $"{order.OrderNo} is {order.Status.StatusName} and is not waiting to be packed."
                });

            var packed = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "PACKED");
            if (packed is null) return BadRequest(new { message = "No PACKED status is configured." });

            var issue = await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "SALE")
                        ?? await _db.MovementTypes.FirstOrDefaultAsync(m => m.TypeKey == "ISSUE");
            if (issue is null) return BadRequest(new { message = "No outbound movement type is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            /* Check every line BEFORE touching any of them, so a short line on
               row 5 does not leave rows 1-4 already deducted. */
            var shortages = new List<object>();
            foreach (var line in order.SalesOrderItems)
            {
                var bal = await _db.StockBalances
                    .FirstOrDefaultAsync(s => s.ProductId == line.ProductId &&
                                              s.LocationId == order.LocationId);
                var onHand = bal?.Quantity ?? 0;
                if (onHand < line.Quantity)
                {
                    var p = await _db.Products.AsNoTracking()
                        .FirstOrDefaultAsync(x => x.ProductId == line.ProductId);
                    shortages.Add(new
                    {
                        sku = p?.Sku,
                        name = p?.ProductName,
                        needed = line.Quantity,
                        onHand,
                        shortBy = line.Quantity - onHand
                    });
                }
            }
            if (shortages.Count > 0)
                return BadRequest(new
                {
                    message = $"{order.OrderNo} cannot be packed -- {shortages.Count} " +
                              $"{(shortages.Count == 1 ? "line is" : "lines are")} short.",
                    shortages
                });

            foreach (var line in order.SalesOrderItems)
            {
                var bal = await _db.StockBalances
                    .FirstAsync(s => s.ProductId == line.ProductId && s.LocationId == order.LocationId);

                bal.Quantity -= line.Quantity;

                _db.StockMovements.Add(new StockMovement
                {
                    ProductId = line.ProductId,
                    LocationId = order.LocationId,
                    MovementTypeId = issue.MovementTypeId,
                    MovedAt = Now(),
                    ReferenceNo = order.OrderNo,
                    Quantity = -line.Quantity,
                    BalanceAfter = bal.Quantity,
                    UserId = CurrentUserId()
                });
            }

            order.StatusId = packed.StatusId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("ORDER_PACKED", "SalesOrder", order.OrderNo,
                $"{order.SalesOrderItems.Count} lines", 1);

            /* -- A6 -- the rep who took it is told, because the customer will
               ring THEM to ask where it is. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept" },
                NotificationKinds.OrderPacked,
                $"Order packed by {CurrentUserName()}",
                $"{order.OrderNo} is packed and ready to go out.",
                url: $"/sales/orders/{order.OrderId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null
                    ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                id,
                orderNo = order.OrderNo,
                message = $"{order.OrderNo} packed and stock updated."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"pack order {id}");
        }
    }
}
