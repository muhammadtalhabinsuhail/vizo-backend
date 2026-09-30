using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /packing screen -- the Order Department's own home page since
/// 23 September, and the front door of dispatching an order.
///
/// This controller used to hold the pre-chain "pick and box" queue: an order
/// arrived PACKED and moved to /dispatch. That screen and its PACKED status
/// were retired on 22 September (migration 21) -- stock now leaves at
/// DISPATCHED, from the place the order screen asks for, and there is nothing
/// left of the old queue worth keeping.
///
/// WHAT REPLACED IT is not a queue at all. The owner's brief: three dropdowns
/// -- salesperson, customer, order -- that narrow each other down and can be
/// filled in from any direction, an order's items shown with their pictures
/// and the Super Admin's own selling price (never the margin a rep may have
/// added), and a quantity the order desk may reduce but never raise. THIS
/// controller only READS that -- salespeople, customers and orders ready to
/// pack, and one order's own detail. The actual dispatch -- moving stock,
/// writing DispatchedQty, rebuilding the invoice's PDF, warning of a shortage
/// -- is the same PATCH /sales/orders/{id}/status the order detail page has
/// always used, extended to accept the quantities this screen lets the order
/// desk adjust (SalesController.SetOrderStatus). One action that changes an
/// order's status belongs in one place.
///
/// "READY TO PACK" means the order sits at INVOICED or AT_ORDER_DEPT. Both are
/// offered, on purpose: OrderWorkflow now lets the order desk dispatch straight
/// from either one, so there is no long "take up the order" click this screen
/// would otherwise be missing.
///
/// EVERY ORDER IS LISTED, NOT ONLY THE READY ONES (the owner, 30 September).
/// The desk kept being asked "where is so-and-so's order?" about orders that
/// were not invoiced yet, and a screen that only showed the ready ones could
/// not answer -- the order simply was not in any of the three boxes. So the
/// dropdowns now hold every salesperson, every customer and every order, each
/// order carrying its status, and the READY rule moved from "what is shown"
/// to "what may be dispatched": the page refuses to dispatch anything not at
/// INVOICED or AT_ORDER_DEPT, and so does SalesController.SetOrderStatus.
///
/// QUANTITIES ARE NOT THE DESK'S TO CHANGE ANY MORE (same day). The lines on
/// this screen are read-only and an order goes out exactly as ordered; the
/// Super Admin or the accountant corrects an order on its own edit screen.
///
/// NO MONEY FOR THE ORDER DESK (26 September, B's HideMoneyFromOrderDesk):
/// the order's total and the line prices come back as 0 for the order-dept
/// role, with moneyHidden = true, the same way DispatchController does it.
/// The Super Admin, who can open this screen too, still sees them.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/packing")]
[ApiController]
[Authorize(Policy = "OrderDept")]
public class PackingController : ApiControllerBase
{
    public PackingController(AppDbContext db, IConfiguration cfg,
        ILogger<PackingController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env)
    {
    }

    /// <summary>The two statuses this screen will pick an order up from. See the class comment.</summary>
    private static readonly string[] Ready = { OrderWorkflow.Invoiced, OrderWorkflow.AtOrderDept };

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS -- the sales and customer dropdowns
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// EVERY salesperson and EVERY customer (the owner, 30 September) -- not
    /// only the ones with something ready to pack, which is what these two
    /// boxes used to hold. The desk has to be able to find any order, and an
    /// order that is not invoiced yet belongs to a rep and a shop that the
    /// old "ready only" lists simply did not contain.
    ///
    /// count is still "how many are ready to pack right now" (INVOICED plus
    /// AT_ORDER_DEPT) -- the one number the page's subtitle shows.
    /// </summary>
    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            /* "assigned to the sales role" -- literally. An order's
               SalesPersonUserId is usually a rep, but not always (an order
               keyed in on somebody's behalf still carries who keyed it in),
               and this box must never offer a name that is not really a
               salesperson. Every active rep, plus any rep since switched off
               who still has orders on the books -- their orders do not stop
               existing when they leave. (The page adds the credited person of
               an order it opens if they are not in this list, so the box never
               shows blank.) */
            var salesPeople = await _db.Employees.AsNoTracking()
                .Where(e => e.User.Role.RoleKey == OrderWorkflow.RoleSales
                            && (e.User.IsActive || e.SalesOrders.Any()))
                .OrderBy(e => e.User.FullName)
                .Select(e => new { id = e.UserId, name = e.User.FullName })
                .ToListAsync();
            var salesIds = salesPeople.Select(r => r.id).ToHashSet();

            /* Which rep(s) each customer belongs to, for the narrowing. Every
               rep credited with one of their orders -- not only the customer's
               assigned rep (Party.SalesPersonUserId), which can differ from
               who actually wrote a given order, and not always exactly one:
               the same shop can have one order from its usual rep and another
               keyed in by somebody else. Without the order pairs, "Pack" on an
               order written by a rep other than the assigned one would set a
               customer the rep box then filtered out of its own list. The
               reverse flow ("pick the customer, the salesperson sets itself")
               only guesses when there is exactly one name to guess. */
            var pairs = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.SalesPersonUserId != null)
                .Select(o => new { o.CustomerUserId, RepId = o.SalesPersonUserId!.Value })
                .Distinct()
                .ToListAsync();
            var orderReps = pairs.GroupBy(x => x.CustomerUserId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.RepId).ToList());

            /* Every active customer (the same set the desk's New Order screen
               offers, OrderLookups below), plus any shop that has an order at
               all -- a customer switched off since still has orders to find. */
            var raw = await _db.Parties.AsNoTracking()
                .Where(p => ((p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive && p.PartyCode != "VZ-C-WALKIN")
                            || p.SalesOrders.Any())
                .OrderBy(p => p.DisplayName ?? p.LegalName)
                .Select(p => new
                {
                    id = p.UserId,
                    name = p.DisplayName ?? p.LegalName,
                    p.SalesPersonUserId,
                    p.CreatedByUserId
                })
                .ToListAsync();

            var customers = raw.Select(c => new
            {
                c.id,
                c.name,
                /* Assigned rep first -- it is the one the reverse fill picks
                   when it is the only one. */
                repIds = new[] { c.SalesPersonUserId, c.CreatedByUserId }
                    .Where(r => r is int v && salesIds.Contains(v)).Select(r => r!.Value)
                    .Concat(orderReps.GetValueOrDefault(c.id) ?? new List<int>())
                    .Distinct().ToList()
            }).ToList();

            return Ok(new
            {
                salesPeople,
                customers,
                count = await _db.SalesOrders.CountAsync(o => Ready.Contains(o.Status.StatusKey))
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the packing lookups");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  THE ORDER DROPDOWN
    // ══════════════════════════════════════════════════════════════════

    /// <summary>How many NOT-ready orders the order dropdown carries at most. See GetPackableOrders.</summary>
    private const int OtherOrdersCap = 300;

    /// <summary>
    /// Every order, whatever its status (the owner, 30 September), narrowed by
    /// whichever of the two upstream boxes is filled in. Neither is required.
    ///
    /// READY ONES FIRST, oldest first -- they are the queue, and the oldest has
    /// waited longest -- then everything else, newest first. Every ready order
    /// always comes back; the rest are capped at OtherOrdersCap, because "every
    /// order" unnarrowed is years of history, and a dropdown of thousands is a
    /// screen that never opens on a phone. Picking a salesperson or a customer
    /// narrows it to what is actually looked for; truncated says the cap was
    /// hit, so the page can say so rather than pretend the list is complete.
    ///
    /// Each row carries its status so the desk can tell at a glance which ones
    /// can be packed. total is 0 for the order desk (moneyHidden).
    /// </summary>
    [HttpGet("orders")]
    public async Task<IActionResult> GetPackableOrders(
        [FromQuery] int? salesPersonId, [FromQuery] int? customerId)
    {
        try
        {
            /* A local, not CurrentRole() inside a query (trap 27). */
            var noMoney = CurrentRole() == OrderWorkflow.RoleOrderDept;

            var rows = _db.SalesOrders.AsNoTracking().AsQueryable();
            if (salesPersonId is not null) rows = rows.Where(o => o.SalesPersonUserId == salesPersonId);
            if (customerId is not null) rows = rows.Where(o => o.CustomerUserId == customerId);

            /* Two cheap id queries decide WHICH orders and in what order; the
               one heavy projection below then runs once over just those ids,
               instead of being written out twice for the two halves. */
            var readyIds = await rows
                .Where(o => Ready.Contains(o.Status.StatusKey))
                .OrderBy(o => o.OrderDate).ThenBy(o => o.OrderId)
                .Select(o => o.OrderId)
                .ToListAsync();
            var otherIds = await rows
                .Where(o => !Ready.Contains(o.Status.StatusKey))
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.OrderId)
                .Select(o => o.OrderId)
                .Take(OtherOrdersCap + 1)
                .ToListAsync();
            var truncated = otherIds.Count > OtherOrdersCap;
            if (truncated) otherIds.RemoveAt(otherIds.Count - 1);

            var ids = readyIds.Concat(otherIds).ToList();
            var position = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

            var fetched = await _db.SalesOrders.AsNoTracking()
                .Where(o => ids.Contains(o.OrderId))
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName),
                    repId = o.SalesPersonUserId,
                    repName = o.SalesPersonUserId == null ? null
                        : _db.Users.Where(u => u.UserId == o.SalesPersonUserId).Select(u => u.FullName).FirstOrDefault(),
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    itemCount = o.SalesOrderItems.Count,
                    totalUnits = o.SalesOrderItems.Sum(l => (int?)l.Quantity) ?? 0,
                    /* A handful of pictures for the closed row -- see the
                       Packing page. The full line list is the {id} call below. */
                    thumbnails = o.SalesOrderItems.OrderBy(l => l.LineNo)
                        .Select(l => l.Product.ImageUrl)
                        .Where(u => u != null)
                        .Take(4)
                        .ToList()
                })
                .ToListAsync();

            var items = fetched
                .OrderBy(o => position[o.id])
                .Select(o => new
                {
                    o.id, o.orderNo, o.customerId, o.customerName, o.repId, o.repName,
                    o.status, o.statusName, o.orderDate,
                    total = noMoney ? 0m : o.total,
                    o.itemCount, o.totalUnits, o.thumbnails
                })
                .ToList();

            return Ok(new { count = items.Count, ready = readyIds.Count, truncated, moneyHidden = noMoney, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load packable orders");
        }
    }

    /// <summary>
    /// One order's full detail: its customer and salesperson (so the screen can
    /// set both dropdowns above it, whichever direction the order was reached
    /// from), and every line with the picture, the quantity ordered, and the
    /// PRICE -- which is deliberately the product's own selling price
    /// (Product.SalePrice, what the Super Admin set), never the order line's
    /// own Rate. "The Order Department must only view the base selling price
    /// defined by the Super Admin," in the owner's own words -- a rep's margin
    /// is not this screen's business. For the order desk itself the price and
    /// the total come back as 0 (moneyHidden) -- no money for the desk at all
    /// since 26 September; the Super Admin on this screen still sees them.
    ///
    /// ANY STATUS since 30 September, not only the ready ones: the Pack button
    /// on the recent list opens any order, and the page shows its lines and
    /// its status either way. Whether it may be DISPATCHED is a separate
    /// question, asked by the page and by SalesController.SetOrderStatus.
    /// </summary>
    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetPackableOrder(int id)
    {
        try
        {
            var noMoney = CurrentRole() == OrderWorkflow.RoleOrderDept;

            var order = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == id)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName),
                    repId = o.SalesPersonUserId,
                    repName = o.SalesPersonUserId == null ? null
                        : _db.Users.Where(u => u.UserId == o.SalesPersonUserId).Select(u => u.FullName).FirstOrDefault(),
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    locationId = o.LocationId,
                    orderDate = o.OrderDate,
                    total = o.TotalAmount,
                    lines = o.SalesOrderItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        orderItemId = l.OrderItemId,
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        imageUrl = l.Product.ImageUrl,
                        packing = l.Product.Packing,
                        qty = l.Quantity,
                        price = l.Product.SalePrice
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (order is null)
                return NotFound(new { message = $"No order with id {id}." });

            return Ok(new
            {
                order.id, order.orderNo, order.customerId, order.customerName,
                order.repId, order.repName, order.status, order.statusName,
                order.locationId, order.orderDate,
                total = noMoney ? 0m : order.total,
                lineTotal = order.lines.Sum(l => (int?)l.qty) ?? 0,
                moneyHidden = noMoney,
                lines = order.lines.Select(l => new
                {
                    l.orderItemId, l.productId, l.name, l.sku, l.imageUrl, l.packing, l.qty,
                    price = noMoney ? 0m : l.price
                }).ToList()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {id} for packing");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  RECENT ORDERS -- the top of the Packing page
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every order CREATED in the last seven days -- today and the six before
    /// it, Pakistan time -- newest first, whatever its status. The owner, 26
    /// September: the order desk wants to see at a glance what came in this
    /// week, not only what is waiting to be packed.
    ///
    /// NO MONEY on these rows, on purpose: order no, date, salesperson,
    /// customer, status and how many items. The order desk sees no totals,
    /// balances or payments anywhere (the same day's rule). Since 30 September
    /// the Packing detail (orders/{id}) zeroes its base prices for the desk as
    /// well; the read-only page below (recent/{id}) still carries them.
    ///
    /// Capped at 300 rows -- a week of this business is a few dozen orders, and
    /// the cap is what stops a busy week turning into a page that never loads.
    ///
    /// PLUS every order sitting at AT_ORDER_DEPT, however old (30 September).
    /// That status is the Super Admin or the accountant handing an order to
    /// the desk -- pending work, not history -- and an order handed over on
    /// the eighth day after it was written would otherwise never appear here
    /// at all. The page lifts those rows into their own "Ready for packing"
    /// group at the top of the list.
    /// </summary>
    [HttpGet("recent")]
    public async Task<IActionResult> GetRecentOrders([FromQuery] int days = 7)
    {
        try
        {
            if (days is < 1 or > 31) days = 7;
            var since = Today().AddDays(-(days - 1));

            var items = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.CreatedAt >= since || o.Status.StatusKey == OrderWorkflow.AtOrderDept)
                /* Handed-over orders first, so the 300 cap can never cut the
                   one kind of row this list must not lose. */
                .OrderByDescending(o => o.Status.StatusKey == OrderWorkflow.AtOrderDept)
                .ThenByDescending(o => o.CreatedAt).ThenByDescending(o => o.OrderId)
                .Take(300)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    orderDate = o.OrderDate,
                    createdAt = o.CreatedAt,
                    customerName = o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName,
                    city = o.CustomerUser.City.CityName,
                    salesPerson = o.SalesPersonUser != null ? o.SalesPersonUser.User.FullName : null,
                    status = o.Status.StatusKey,
                    statusName = o.Status.StatusName,
                    itemCount = o.SalesOrderItems.Count,
                    units = o.SalesOrderItems.Sum(l => (int?)l.Quantity) ?? 0
                })
                .ToListAsync();

            return Ok(new { since, days, count = items.Count, items });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the recent orders");
        }
    }

    /// <summary>
    /// One order, read-only, for the order desk -- any status, not only the
    /// ones ready to pack. The ordinary order screen (/sales/orders/{id}) is
    /// built around money -- rates with the rep's margin, what has been paid,
    /// the customer's balance and limit -- and around buttons the desk may not
    /// press; this is the same order with its items, pictures, quantities and
    /// the base price the Packing screen already shows, and where it has got to.
    /// </summary>
    [HttpGet("recent/{id:int}")]
    public async Task<IActionResult> GetOrderReadOnly(int id)
    {
        try
        {
            var o = await _db.SalesOrders.AsNoTracking()
                .Where(x => x.OrderId == id)
                .Select(x => new
                {
                    id = x.OrderId,
                    orderNo = x.OrderNo,
                    orderDate = x.OrderDate,
                    createdAt = x.CreatedAt,
                    deliveryDate = x.DeliveryDate,
                    customerName = x.CustomerUser.DisplayName ?? x.CustomerUser.LegalName,
                    customerCode = x.CustomerUser.PartyCode,
                    customerPhone = x.CustomerUser.User.Phone,
                    customerAddress = x.CustomerUser.AddressLine,
                    city = x.CustomerUser.City.CityName,
                    salesPerson = x.SalesPersonUser != null ? x.SalesPersonUser.User.FullName : null,
                    createdBy = x.CreatedByUser.FullName,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    location = x.Location.LocationName,
                    notes = x.Notes,
                    invoiceNo = x.SalesInvoice != null ? x.SalesInvoice.InvoiceNo : null,
                    channel = x.Deliveries.OrderByDescending(d => d.DeliveryId).Select(d => d.Channel.ChannelName).FirstOrDefault(),
                    carrier = x.Deliveries.OrderByDescending(d => d.DeliveryId)
                        .Select(d => d.Courier != null ? d.Courier.CourierName : null).FirstOrDefault(),
                    trackingNo = x.Deliveries.OrderByDescending(d => d.DeliveryId).Select(d => d.TrackingNo).FirstOrDefault(),
                    dispatchedOn = x.Deliveries.OrderByDescending(d => d.DeliveryId).Select(d => (DateOnly?)d.BookedDate).FirstOrDefault(),
                    deliveredOn = x.Deliveries.OrderByDescending(d => d.DeliveryId).Select(d => d.DeliveredDate).FirstOrDefault(),
                    lines = x.SalesOrderItems.OrderBy(l => l.LineNo).Select(l => new
                    {
                        id = l.OrderItemId,
                        productId = l.ProductId,
                        name = l.Product.ProductName,
                        sku = l.Product.Sku,
                        imageUrl = l.Product.ImageUrl,
                        packing = l.Product.Packing,
                        qty = l.Quantity,
                        dispatchedQty = l.DispatchedQty,
                        price = l.Product.SalePrice
                    }).ToList()
                })
                .FirstOrDefaultAsync();

            if (o is null) return NotFound(new { message = $"No order with id {id}." });

            return Ok(new
            {
                o.id, o.orderNo, o.orderDate, o.createdAt, o.deliveryDate,
                o.customerName, o.customerCode, o.customerPhone, o.customerAddress, o.city,
                o.salesPerson, o.createdBy, o.status, o.statusName, o.location, o.notes, o.invoiceNo,
                o.channel, o.carrier, o.trackingNo, o.dispatchedOn, o.deliveredOn,
                itemCount = o.lines.Count,
                units = o.lines.Sum(l => l.qty),
                totalAtBase = o.lines.Sum(l => l.qty * l.price),
                o.lines
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  TAKING AN ORDER -- the order desk's own pickers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// What the order desk's New Order screen needs, and nothing it may not see:
    /// every active salesperson; every customer with the rep(s) they belong to
    /// (the rep they are assigned to, and the rep who opened them -- the same
    /// "whose customer is this" rule PartiesController.MyPartiesOnly uses) so
    /// picking either box can fill the other; and the catalogue with the SELLING
    /// price and stock -- no cost, no duty, no limits, no balances.
    ///
    /// GET /sales/lookups would answer too, with those figures zeroed for this
    /// role; this is lighter and says exactly what the screen uses.
    /// </summary>
    [HttpGet("order-lookups")]
    public async Task<IActionResult> OrderLookups()
    {
        try
        {
            var salesPeople = await _db.Employees.AsNoTracking()
                .Where(e => e.User.Role.RoleKey == OrderWorkflow.RoleSales && e.User.IsActive)
                .OrderBy(e => e.User.FullName)
                .Select(e => new { id = e.UserId, name = e.User.FullName })
                .ToListAsync();
            var repIds = salesPeople.Select(r => r.id).ToHashSet();

            var raw = await _db.Parties.AsNoTracking()
                .Where(p => (p.User.RoleId == 5 || p.User.RoleId == 7) && p.User.IsActive && p.PartyCode != "VZ-C-WALKIN")
                .OrderBy(p => p.DisplayName ?? p.LegalName)
                .Select(p => new
                {
                    id = p.UserId,
                    code = p.PartyCode,
                    name = p.DisplayName ?? p.LegalName,
                    city = p.City.CityName,
                    phone = p.User.Phone,
                    p.SalesPersonUserId,
                    p.CreatedByUserId
                })
                .ToListAsync();

            var customers = raw.Select(c => new
            {
                c.id, c.code, c.name, c.city, c.phone,
                /* Assigned rep first -- it is the one the reverse fill picks. */
                repIds = new[] { c.SalesPersonUserId, c.CreatedByUserId }
                    .Where(r => r is int v && repIds.Contains(v)).Select(r => r!.Value).Distinct().ToList()
            }).ToList();

            var methods = await _db.PaymentMethods.AsNoTracking()
                .Where(m => m.IsActive && m.IsForReceiving)
                .OrderBy(m => m.MethodId)
                .Select(m => new { id = m.MethodId, key = m.MethodKey, name = m.MethodName })
                .ToListAsync();

            var products = await _db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProductName)
                .Select(p => new
                {
                    id = p.ProductId,
                    sku = p.Sku,
                    name = p.ProductName,
                    packing = p.Packing,
                    salePrice = p.SalePrice,
                    taxRatePercent = p.TaxRatePercent,
                    imageUrl = p.ImageUrl,
                    stock = p.StockBalances.Sum(s => (int?)s.Quantity) ?? 0
                })
                .ToListAsync();

            return Ok(new { salesPeople, customers, methods, products, maxMarginPercent = 10 });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the order desk's pickers");
        }
    }
}
