using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /dispatch screen -- packed orders leaving the building.
///
/// THIS is where the delivery route is chosen, and the choice matters more than
/// it looks: the channel decides WHO is allowed to confirm the delivery later
/// and how soon the reminder starts nagging. See DeliveryController for the
/// confirming half.
///
///   local     -- Karachi, own team
///   online    -- online courier (tracking number, usually COD)
///   cargo     -- local cargo company (bilty)
///   logistics -- heavy freight
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/dispatch")]
[ApiController]
[Authorize(Policy = "OrderDept")]
public class DispatchController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public DispatchController(AppDbContext db, IConfiguration cfg,
        ILogger<DispatchController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  THE QUEUE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Orders that have gone out and still have no courier booked against them.
    ///
    /// It used to read "packed": PACKED was the old pre-chain status the
    /// retired /packing screen wrote, and migration 21 removed it along with
    /// the three chain steps the owner took out. The queue now sits AFTER the
    /// dispatch step rather than before it -- pressing Dispatched on the order
    /// is what takes the stock off the shelf, and this screen is where the
    /// bilty, the parcels and the COD are recorded once the goods are on a van.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetDispatchQueue([FromQuery] int? locationId, [FromQuery] string? q)
    {
        try
        {
            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey == "DISPATCHED" && !o.Deliveries.Any());

            if (locationId is not null) rows = rows.Where(o => o.LocationId == locationId);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(o => o.OrderNo.ToLower().Contains(term) ||
                                       (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName).ToLower().Contains(term));
            }

            var items = await rows
                .OrderBy(o => o.DeliveryDate ?? o.OrderDate).ThenBy(o => o.OrderId)
                .Select(o => new
                {
                    id = o.OrderId,
                    orderNo = o.OrderNo,
                    customerId = o.CustomerUserId,
                    customerName = (o.CustomerUser.DisplayName ?? o.CustomerUser.LegalName),
                    customerPhone = o.CustomerUser.User.Phone,
                    address = o.CustomerUser.AddressLine,
                    city = o.CustomerUser.City.CityName,
                    cityId = o.CustomerUser.CityId,
                    province = o.CustomerUser.City.Province.ProvinceName,
                    locationId = o.LocationId,
                    locationCityId = o.Location.CityId,
                    location = o.Location.LocationName,
                    orderDate = o.OrderDate,
                    deliveryDate = o.DeliveryDate,
                    total = o.TotalAmount,
                    paymentMethod = o.Method.MethodKey,
                    itemCount = o.SalesOrderItems.Count,
                    totalUnits = o.SalesOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    invoiceId = o.SalesInvoice != null ? (int?)o.SalesInvoice.InvoiceId : null,
                    invoiceNo = o.SalesInvoice != null ? o.SalesInvoice.InvoiceNo : null,

                    /* Anything still unpaid rides as COD unless the office says
                       otherwise -- the screen pre-fills this figure. */
                    paidAmount = o.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .ToListAsync();

            /* THE ORDER DESK SEES NO MONEY (the owner, 26 Sep; B's
               HideMoneyFromOrderDesk on the order screens). This queue is the
               desk's own screen and it showed each order's total and the COD
               to collect. For the desk those figures are zero and it is told
               only WHETHER cash is to be taken at the door; the COD itself is
               worked out on the server when it books (Dispatch, below). A
               local, not CurrentRole() inside a query (trap 27). */
            var noMoney = CurrentRole() == OrderWorkflow.RoleOrderDept;
            var suggest = await SuggestChannels(items.Select(o => (o.cityId, o.locationCityId)).ToList());

            var today = Today();
            var shaped = items.Select(o => new
            {
                o.id,
                o.orderNo,
                o.customerId,
                o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerPhone,
                o.address,
                o.city,
                o.province,
                o.locationId,
                o.location,
                o.orderDate,
                o.deliveryDate,
                total = noMoney ? 0m : o.total,
                o.paymentMethod,
                o.itemCount,
                o.totalUnits,
                o.invoiceId,
                o.invoiceNo,
                paidAmount = noMoney ? 0m : o.paidAmount,
                suggestedCod = noMoney ? 0m : SuggestedCod(o.paymentMethod, o.total, o.paidAmount),
                collectsCash = SuggestedCod(o.paymentMethod, o.total, o.paidAmount) > 0,
                suggestedChannelId = suggest(o.cityId, o.locationCityId),
                waitingDays = today.DayNumber - o.orderDate.DayNumber,
                isLate = o.deliveryDate != null && o.deliveryDate < today
            }).ToList();

            return Ok(new
            {
                waiting = shaped.Count,
                late = shaped.Count(o => o.isLate),
                moneyHidden = noMoney,
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the dispatch queue");
        }
    }

    /// <summary>
    /// Which channel the booking form should START on for each order. Only a
    /// starting point -- the order desk can pick any channel -- but a good one
    /// saves a click on every parcel.
    ///
    /// It used to be `order.city === "Karachi"` in the browser, which never
    /// matched anything: city names carry the country ("Karachi - Pakistan",
    /// HANDOFF trap 22), so every order opened on cargo. And it hard-coded the
    /// one city this company happens to have its own riders in. The rule is now
    /// read from the data:
    ///
    ///   1. The customer is in the SAME CITY as the place the goods left from
    ///      -> the channel the salesman confirms himself (ConfirmedByRole =
    ///      sales): the own-team, by-hand delivery. That is what "local" means,
    ///      in Karachi and equally in Lahore.
    ///   2. Otherwise -> whichever channel this customer's CITY was last booked
    ///      on (the by-hand channel excluded: it cannot reach another city).
    ///      The desk's own habit per destination -- Islamabad goes by freight,
    ///      Multan by cargo -- and it follows them if the habit changes.
    ///   3. A city never shipped to before -> the first active channel that is
    ///      not by hand.
    /// </summary>
    private async Task<Func<int, int, int?>> SuggestChannels(List<(int cityId, int locationCityId)> orders)
    {
        var channels = await _db.DeliveryChannels.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.ChannelId)
            .Select(c => new { c.ChannelId, byHand = c.ConfirmedByRole.RoleKey == OrderWorkflow.RoleSales })
            .ToListAsync();

        var handId = channels.FirstOrDefault(c => c.byHand)?.ChannelId;
        var fallback = channels.FirstOrDefault(c => !c.byHand)?.ChannelId ?? handId;
        var usable = channels.Where(c => !c.byHand).Select(c => c.ChannelId).ToHashSet();

        var cityIds = orders.Select(o => o.cityId).Distinct().ToList();
        var history = cityIds.Count == 0
            ? new Dictionary<int, int>()
            : (await _db.Deliveries.AsNoTracking()
                .Where(d => cityIds.Contains(d.Order.CustomerUser.CityId))
                .Select(d => new { cityId = d.Order.CustomerUser.CityId, d.ChannelId, d.DeliveryId })
                .ToListAsync())
              .Where(d => usable.Contains(d.ChannelId))
              .GroupBy(d => d.cityId)
              .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DeliveryId).First().ChannelId);

        return (cityId, locationCityId) =>
            cityId == locationCityId && handId is not null ? handId
            : history.TryGetValue(cityId, out var last) ? last
            : fallback;
    }

    // ══════════════════════════════════════════════════════════════════
    //  DISPATCHING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Books the courier for an order that has already been dispatched.
    ///
    /// It used to move the order to DISPATCHED itself, from the old PACKED
    /// status. That step belongs to the chain now (and is where the stock
    /// leaves), so this action books the delivery and leaves the status alone.
    ///
    /// The channel picked here is what later decides who may confirm arrival, so
    /// it is validated against the DeliveryChannel table rather than accepted as
    /// a free string. Channels flagged RequiresBilty refuse to book without a
    /// tracking / bilty number, because a cargo booking with no bilty cannot be
    /// chased when it goes missing.
    /// </summary>
    [HttpPost("{id:int}/dispatch")]
    public async Task<IActionResult> Dispatch(int id, [FromBody] DispatchRequest body)
    {
        try
        {
            var order = await _db.SalesOrders
                .Include(o => o.Status)
                .Include(o => o.Deliveries)
                .FirstOrDefaultAsync(o => o.OrderId == id);

            if (order is null) return NotFound(new { message = $"No order with id {id}." });
            if (order.Status.StatusKey != "DISPATCHED")
                return BadRequest(new
                {
                    message = $"{order.OrderNo} is {order.Status.StatusName}. " +
                              "Mark the order Dispatched on the order screen first -- that is where the " +
                              "stock comes off the shelf -- then book the courier here."
                });
            if (order.Deliveries.Any())
                return BadRequest(new { message = $"{order.OrderNo} already has a delivery booked." });

            /* The checks live in CheckBooking since 2 October, so that editing a
               booked delivery (UpdateDelivery, below) refuses exactly what
               booking refuses -- including the order desk's COD rule. */
            var (channel, codAmount, problem) = await CheckBooking(id, body, null);
            if (problem is not null || channel is null)
                return BadRequest(new { message = problem ?? "Pick a valid delivery channel." });

            var booked = await _db.DeliveryStatuses.FirstOrDefaultAsync(s => s.StatusKey == "BOOKED");
            if (booked is null)
                return BadRequest(new { message = "The BOOKED delivery status is not configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var delivery = new Delivery
            {
                DeliveryNo = await NextNumber("DLV"),
                OrderId = order.OrderId,
                InvoiceId = await _db.SalesInvoices
                    .Where(i => i.OrderId == order.OrderId)
                    .Select(i => (int?)i.InvoiceId)
                    .FirstOrDefaultAsync(),
                ChannelId = channel.ChannelId,
                CourierId = body.CourierId,
                TrackingNo = body.TrackingNo,
                BookedDate = body.BookedDate ?? Today(),
                ExpectedDate = body.ExpectedDate,
                DeliveredDate = null,
                StatusId = booked.StatusId,
                Parcels = body.Parcels,
                WeightKg = body.WeightKg,
                CodAmount = codAmount,
                IsCodSettled = false,
                BookingCharge = body.BookingCharge,
                RemindersSent = 0,
                ConfirmedByUserId = null,
                Notes = body.Notes
            };
            _db.Deliveries.Add(delivery);

            /* The order is already DISPATCHED -- the chain moved it, and took
               the stock with it. Nothing to change here but the paperwork. */
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("ORDER_DISPATCHED", "SalesOrder", order.OrderNo,
                $"{channel.ChannelName}{(body.TrackingNo is null ? "" : $" / {body.TrackingNo}")}", 1);

            /* -- A7 -- Accounts is included: a dispatch is the point a COD
               order starts being money somebody has to chase. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept", "accountant" },
                NotificationKinds.OrderDispatched,
                $"Order dispatched by {CurrentUserName()}",
                $"{order.OrderNo} has left via {channel.ChannelName}" +
                (string.IsNullOrWhiteSpace(body.TrackingNo) ? "." : $", tracking {body.TrackingNo}."),
                url: $"/delivery/{delivery.DeliveryId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null
                    ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                id,
                deliveryId = delivery.DeliveryId,
                deliveryNo = delivery.DeliveryNo,
                message = $"{order.OrderNo} dispatched via {channel.ChannelName}. " +
                          $"Confirmation is owned by the {channel.ChannelName} route."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"dispatch order {id}");
        }
    }

    /// <summary>
    /// The checks a booking has to pass, shared by booking (Dispatch) and by
    /// editing a booking (UpdateDelivery) so the two can never drift apart.
    /// Returns the channel, the COD that will actually be written, and the
    /// sentence to refuse with (null when it passes).
    ///
    /// THE ORDER DESK'S COD. The desk books without seeing money, so it cannot
    /// type the COD:
    ///   - booking: the server charges exactly what the queue would have
    ///     suggested -- whatever is unpaid on the order, nothing on credit;
    ///   - editing (existingCod given): the figure already on the delivery is
    ///     KEPT. The desk opened the form to fix a bilty or a parcel count, and
    ///     re-working the COD behind its back would silently overwrite a figure
    ///     the Super Admin or the accountant may have set on purpose. Whoever
    ///     can see money changes the COD; the desk never does, in either
    ///     direction.
    /// </summary>
    private async Task<(DeliveryChannel? channel, decimal cod, string? problem)> CheckBooking(
        int orderId, DispatchRequest body, decimal? existingCod)
    {
        var channel = await _db.DeliveryChannels
            .FirstOrDefaultAsync(c => c.ChannelId == body.ChannelId && c.IsActive);
        if (channel is null) return (null, 0m, "Pick a valid delivery channel.");

        if (channel.RequiresBilty && string.IsNullOrWhiteSpace(body.TrackingNo))
            return (null, 0m, $"{channel.ChannelName} needs a bilty or tracking number before it can be booked.");

        if (body.CourierId is not null &&
            !await _db.Couriers.AnyAsync(c => c.CourierId == body.CourierId && c.IsActive))
            return (null, 0m, "Pick a valid courier.");

        var codAmount = body.CodAmount;
        if (CurrentRole() == OrderWorkflow.RoleOrderDept)
        {
            if (existingCod is not null)
            {
                codAmount = existingCod.Value;
            }
            else
            {
                var o = await _db.SalesOrders.AsNoTracking().Where(x => x.OrderId == orderId)
                    .Select(x => new
                    {
                        method = x.Method.MethodKey,
                        total = x.TotalAmount,
                        paid = x.CollectionAllocations
                            .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                            .Sum(a => (decimal?)a.Amount) ?? 0m
                    })
                    .FirstAsync();
                codAmount = SuggestedCod(o.method, o.total, o.paid);
            }
        }

        if (body.Parcels < 1) return (null, 0m, "A dispatch needs at least one parcel.");
        if (codAmount < 0) return (null, 0m, "COD cannot be negative.");

        return (channel, codAmount, null);
    }

    // ══════════════════════════════════════════════════════════════════
    //  ONE ORDER, FOR THE "HOW IS IT GOING" FORM -- and editing its booking
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// One order in the same shape the queue (GetDispatchQueue) gives each row
    /// -- so the booking form (components/delivery/dispatch-sheet.tsx) opens
    /// on it with no second shape to maintain -- but for ANY status, plus the
    /// delivery already booked against it, if there is one.
    ///
    /// Why it exists (the owner, 2 October): on the Packing page, Next used to
    /// dispatch the order on the spot and only THEN show the booking form. Now
    /// Next only opens the form, and the order is dispatched when the form's
    /// own Dispatch button is pressed -- so the form has to open on an order
    /// that is still at "Processing in Order Dept", which the queue (dispatched
    /// orders only) does not carry. And the Packing page's Edit button reopens
    /// the same form on a dispatched order, filled in from `delivery`.
    ///
    /// No money for the order desk, exactly as the queue: totals, paid and the
    /// suggested COD are zero, and so is the booked COD; collectsCash still
    /// says whether anything is to be taken at the door.
    /// </summary>
    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetDispatchOrder(int id)
    {
        try
        {
            var o = await _db.SalesOrders.AsNoTracking()
                .Where(x => x.OrderId == id)
                .Select(x => new
                {
                    id = x.OrderId,
                    orderNo = x.OrderNo,
                    status = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    customerId = x.CustomerUserId,
                    customerName = (x.CustomerUser.DisplayName ?? x.CustomerUser.LegalName),
                    customerPhone = x.CustomerUser.User.Phone,
                    address = x.CustomerUser.AddressLine,
                    city = x.CustomerUser.City.CityName,
                    cityId = x.CustomerUser.CityId,
                    province = x.CustomerUser.City.Province.ProvinceName,
                    locationId = x.LocationId,
                    locationCityId = x.Location.CityId,
                    location = x.Location.LocationName,
                    orderDate = x.OrderDate,
                    deliveryDate = x.DeliveryDate,
                    total = x.TotalAmount,
                    paymentMethod = x.Method.MethodKey,
                    itemCount = x.SalesOrderItems.Count,
                    totalUnits = x.SalesOrderItems.Sum(i => (int?)i.Quantity) ?? 0,
                    invoiceId = x.SalesInvoice != null ? (int?)x.SalesInvoice.InvoiceId : null,
                    invoiceNo = x.SalesInvoice != null ? x.SalesInvoice.InvoiceNo : null,
                    paidAmount = x.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m
                })
                .FirstOrDefaultAsync();

            if (o is null) return NotFound(new { message = $"No order with id {id}." });

            /* The latest delivery -- an order normally has at most one (Dispatch
               refuses a second), but a parcel returned to sender and re-sent
               would leave two, and it is the newest one that is being edited. */
            var d = await _db.Deliveries.AsNoTracking()
                .Where(x => x.OrderId == id)
                .OrderByDescending(x => x.DeliveryId)
                .Select(x => new
                {
                    id = x.DeliveryId,
                    deliveryNo = x.DeliveryNo,
                    channelId = x.ChannelId,
                    courierId = x.CourierId,
                    trackingNo = x.TrackingNo,
                    bookedDate = x.BookedDate,
                    expectedDate = x.ExpectedDate,
                    deliveredDate = x.DeliveredDate,
                    parcels = x.Parcels,
                    weightKg = x.WeightKg,
                    codAmount = x.CodAmount,
                    isCodSettled = x.IsCodSettled,
                    bookingCharge = x.BookingCharge,
                    notes = x.Notes,
                    statusKey = x.Status.StatusKey,
                    statusName = x.Status.StatusName,
                    isOpen = x.Status.IsOpen
                })
                .FirstOrDefaultAsync();

            var noMoney = CurrentRole() == OrderWorkflow.RoleOrderDept;
            var suggest = await SuggestChannels(new List<(int cityId, int locationCityId)> { (o.cityId, o.locationCityId) });
            var today = Today();

            return Ok(new
            {
                o.id,
                o.orderNo,
                o.status,
                o.statusName,
                o.customerId,
                o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerPhone,
                o.address,
                o.city,
                o.province,
                o.locationId,
                o.location,
                o.orderDate,
                o.deliveryDate,
                total = noMoney ? 0m : o.total,
                o.paymentMethod,
                o.itemCount,
                o.totalUnits,
                o.invoiceId,
                o.invoiceNo,
                paidAmount = noMoney ? 0m : o.paidAmount,
                suggestedCod = noMoney ? 0m : SuggestedCod(o.paymentMethod, o.total, o.paidAmount),
                collectsCash = SuggestedCod(o.paymentMethod, o.total, o.paidAmount) > 0,
                suggestedChannelId = suggest(o.cityId, o.locationCityId),
                waitingDays = today.DayNumber - o.orderDate.DayNumber,
                isLate = o.deliveryDate != null && o.deliveryDate < today,
                moneyHidden = noMoney,
                delivery = d is null ? null : new
                {
                    d.id,
                    d.deliveryNo,
                    d.channelId,
                    d.courierId,
                    d.trackingNo,
                    d.bookedDate,
                    d.expectedDate,
                    d.deliveredDate,
                    d.parcels,
                    d.weightKg,
                    codAmount = noMoney ? 0m : d.codAmount,
                    d.isCodSettled,
                    bookingCharge = noMoney ? 0m : d.bookingCharge,
                    d.notes,
                    d.statusKey,
                    d.statusName,
                    /* The same three refusals UpdateDelivery makes, so the page
                       can say so before the form is even opened. */
                    editable = d.isOpen && d.deliveredDate == null && !d.isCodSettled
                }
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"load order {id} for dispatch");
        }
    }

    /// <summary>
    /// Changes the details of a delivery that is already booked -- the
    /// Packing page's Edit button (the owner, 2 October): channel, carrier,
    /// bilty / tracking, expected date, parcels, weight, booking charge, note,
    /// and the COD for whoever may see money.
    ///
    /// ONLY THE PAPERWORK. The order's status is not touched (it stays
    /// DISPATCHED), no stock moves, no second delivery is created, and nobody
    /// is told the order "has been dispatched" again. The booked date stays
    /// the day it was booked -- the form has no such field, and editing a
    /// tracking number on Thursday does not mean the parcel left on Thursday.
    ///
    /// Refused once the delivery is closed: confirmed delivered, returned to
    /// sender, or its COD settled -- by then the record is evidence (who
    /// signed, what the courier paid over), not a booking to correct. The
    /// validation is the booking's own (CheckBooking), and the order desk still
    /// never sets the COD: its edit keeps the figure already on the delivery.
    /// </summary>
    [HttpPut("deliveries/{deliveryId:int}")]
    public async Task<IActionResult> UpdateDelivery(int deliveryId, [FromBody] DispatchRequest body)
    {
        try
        {
            var delivery = await _db.Deliveries
                .Include(d => d.Status)
                .Include(d => d.Order).ThenInclude(o => o.Status)
                .FirstOrDefaultAsync(d => d.DeliveryId == deliveryId);

            if (delivery is null) return NotFound(new { message = $"No delivery with id {deliveryId}." });
            if (delivery.DeliveredDate is not null || delivery.Status.StatusKey == "DELIVERED")
                return BadRequest(new { message = $"{delivery.DeliveryNo} has already been delivered, so its details can no longer be changed." });
            if (!delivery.Status.IsOpen)
                return BadRequest(new { message = $"{delivery.DeliveryNo} is {delivery.Status.StatusName.ToLowerInvariant()}, so its details can no longer be changed." });
            if (delivery.IsCodSettled)
                return BadRequest(new { message = $"The COD on {delivery.DeliveryNo} has been settled, so its details can no longer be changed." });
            if (delivery.Order.Status.StatusKey != OrderWorkflow.Dispatched)
                return BadRequest(new
                {
                    message = $"{delivery.Order.OrderNo} is {delivery.Order.Status.StatusName.ToLowerInvariant()}, " +
                              "so its delivery can no longer be changed here."
                });

            var (channel, codAmount, problem) = await CheckBooking(delivery.OrderId, body, delivery.CodAmount);
            if (problem is not null || channel is null)
                return BadRequest(new { message = problem ?? "Pick a valid delivery channel." });

            delivery.ChannelId = channel.ChannelId;
            delivery.CourierId = body.CourierId;
            delivery.TrackingNo = string.IsNullOrWhiteSpace(body.TrackingNo) ? null : body.TrackingNo.Trim();
            delivery.ExpectedDate = body.ExpectedDate;
            delivery.Parcels = body.Parcels;
            delivery.WeightKg = body.WeightKg;
            delivery.CodAmount = codAmount;
            /* The booking charge is the courier's own figure the form sends with
               the carrier picked -- a carrier changed here brings its charge with
               it, the same as at booking. */
            delivery.BookingCharge = body.BookingCharge;
            delivery.Notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();

            await _db.SaveChangesAsync();

            await Log("DELIVERY_UPDATED", "Delivery", delivery.DeliveryNo,
                $"{delivery.Order.OrderNo}: {channel.ChannelName}" +
                $"{(delivery.TrackingNo is null ? "" : $" / {delivery.TrackingNo}")}, " +
                $"{delivery.Parcels} {(delivery.Parcels == 1 ? "parcel" : "parcels")}", 1);

            return Ok(new
            {
                id = delivery.OrderId,
                deliveryId = delivery.DeliveryId,
                deliveryNo = delivery.DeliveryNo,
                message = $"{delivery.DeliveryNo} updated -- {delivery.Order.OrderNo} going via {channel.ChannelName}."
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"update delivery {deliveryId}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                channels = await _db.DeliveryChannels.AsNoTracking()
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.ChannelId)
                    .Select(c => new
                    {
                        id = c.ChannelId,
                        key = c.ChannelKey,
                        name = c.ChannelName,
                        description = c.Description,
                        requiresBilty = c.RequiresBilty,
                        remindAfterDays = c.RemindAfterDays,
                        /* The booking form says "then every N hours" -- it read
                           a field this list never sent, and printed "undefined". */
                        remindEveryHours = c.RemindEveryHours,
                        confirmedByRole = c.ConfirmedByRole.RoleKey,
                        confirmedByRoleName = c.ConfirmedByRole.RoleName,

                        /* Only the carriers wired to this channel -- picking an
                           air courier for heavy freight is a data error the form
                           should not allow in the first place. */
                        carriers = c.Couriers
                            .Where(x => x.IsActive)
                            .Select(x => new
                            {
                                id = x.CourierId,
                                name = x.CourierName,
                                shortName = x.ShortName,
                                bookingCharge = x.BookingCharge,
                                codFeePercent = x.CodFeePercent,
                                codSettlementDays = x.CodSettlementDays
                            }).ToList()
                    })
                    .ToListAsync(),
                locations = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderBy(l => l.LocationName)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load dispatch lookups");
        }
    }


    /// <summary>What is to be taken at the door: the unpaid balance, or nothing on credit.</summary>
    private static decimal SuggestedCod(string paymentMethod, decimal total, decimal paid) =>
        paymentMethod == "CREDIT" ? 0m : Math.Max(0m, total - paid);

    // ══════════════════════════ request bodies ══════════════════════════

    public record DispatchRequest(
        int ChannelId, int? CourierId, string? TrackingNo,
        DateOnly? BookedDate, DateOnly? ExpectedDate,
        int Parcels, decimal WeightKg, decimal CodAmount, decimal BookingCharge,
        string? Notes);
}