using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

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
    public DispatchController(AppDbContext db, IConfiguration cfg,
        ILogger<DispatchController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    // ══════════════════════════════════════════════════════════════════
    //  THE QUEUE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Orders that are packed and still have no delivery booked.</summary>
    [HttpGet]
    public async Task<IActionResult> GetDispatchQueue([FromQuery] int? locationId, [FromQuery] string? q)
    {
        try
        {
            var rows = _db.SalesOrders.AsNoTracking()
                .Where(o => o.Status.StatusKey == "PACKED" && !o.Deliveries.Any());

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
                    customerPhone = o.CustomerUser.User.Phone,
                    address = o.CustomerUser.AddressLine,
                    city = o.CustomerUser.City.CityName,
                    province = o.CustomerUser.City.Province.ProvinceName,
                    locationId = o.LocationId,
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

            var today = Today();
            var shaped = items.Select(o => new
            {
                o.id, o.orderNo, o.customerId, o.customerName,
                customerInitials = Initials(o.customerName),
                o.customerPhone, o.address, o.city, o.province,
                o.locationId, o.location, o.orderDate, o.deliveryDate,
                o.total, o.paymentMethod, o.itemCount, o.totalUnits,
                o.invoiceId, o.invoiceNo, o.paidAmount,
                suggestedCod = o.paymentMethod == "CREDIT" ? 0m : o.total - o.paidAmount,
                waitingDays = today.DayNumber - o.orderDate.DayNumber,
                isLate = o.deliveryDate != null && o.deliveryDate < today
            }).ToList();

            return Ok(new
            {
                waiting = shaped.Count,
                late = shaped.Count(o => o.isLate),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the dispatch queue");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  DISPATCHING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Books a delivery for a packed order and moves it to DISPATCHED.
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
            if (order.Status.StatusKey != "PACKED")
                return BadRequest(new
                {
                    message = $"{order.OrderNo} is {order.Status.StatusName}. Only a packed order can be dispatched."
                });
            if (order.Deliveries.Any())
                return BadRequest(new { message = $"{order.OrderNo} already has a delivery booked." });

            var channel = await _db.DeliveryChannels
                .FirstOrDefaultAsync(c => c.ChannelId == body.ChannelId && c.IsActive);
            if (channel is null) return BadRequest(new { message = "Pick a valid delivery channel." });

            if (channel.RequiresBilty && string.IsNullOrWhiteSpace(body.TrackingNo))
                return BadRequest(new
                {
                    message = $"{channel.ChannelName} needs a bilty or tracking number before it can be booked."
                });

            if (body.CourierId is not null &&
                !await _db.Couriers.AnyAsync(c => c.CourierId == body.CourierId && c.IsActive))
                return BadRequest(new { message = "Pick a valid courier." });

            if (body.Parcels < 1)
                return BadRequest(new { message = "A dispatch needs at least one parcel." });
            if (body.CodAmount < 0)
                return BadRequest(new { message = "COD cannot be negative." });

            var booked = await _db.DeliveryStatuses.FirstOrDefaultAsync(s => s.StatusKey == "BOOKED");
            var dispatched = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DISPATCHED");
            if (booked is null || dispatched is null)
                return BadRequest(new { message = "BOOKED / DISPATCHED statuses are not configured." });

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
                CodAmount = body.CodAmount,
                IsCodSettled = false,
                BookingCharge = body.BookingCharge,
                RemindersSent = 0,
                ConfirmedByUserId = null,
                Notes = body.Notes
            };
            _db.Deliveries.Add(delivery);

            order.StatusId = dispatched.StatusId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("ORDER_DISPATCHED", "SalesOrder", order.OrderNo,
                $"{channel.ChannelName}{(body.TrackingNo is null ? "" : $" / {body.TrackingNo}")}", 1);

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
                    .Select(c => new
                    {
                        id = c.ChannelId,
                        key = c.ChannelKey,
                        name = c.ChannelName,
                        description = c.Description,
                        requiresBilty = c.RequiresBilty,
                        remindAfterDays = c.RemindAfterDays,
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

    // ════════════════════════ numbering helper ════════════════════════

    private async Task<string> NextNumber(string prefix)
    {
        var series = await _db.DocumentSeries.FirstOrDefaultAsync(s => s.Prefix == prefix);
        if (series is null) return $"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        var n = series.NextNumber;
        series.NextNumber = n + 1;
        await _db.SaveChangesAsync();

        var year = series.IncludeYear ? $"{DateTime.UtcNow:yyyy}-" : "";
        return $"{series.Prefix}-{year}{n.ToString().PadLeft(series.Padding, '0')}";
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record DispatchRequest(
        int ChannelId, int? CourierId, string? TrackingNo,
        DateOnly? BookedDate, DateOnly? ExpectedDate,
        int Parcels, decimal WeightKg, decimal CodAmount, decimal BookingCharge,
        string? Notes);
}
