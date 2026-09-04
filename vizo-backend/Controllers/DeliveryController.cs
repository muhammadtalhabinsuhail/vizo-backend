using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The /delivery screen, and the confirmation step behind it.
///
/// THE DESIGN POINT: delivery confirmation is owned by the CHANNEL, not by a
/// person. There are four routes -- Karachi own-team, online courier, local
/// cargo, heavy freight -- and each names the role allowed to confirm it in
/// DeliveryChannel.ConfirmedByRoleId, with its own reminder timer. The confirm
/// button only appears for the role that owns that channel, and this controller
/// enforces that server-side rather than trusting the screen to hide it.
///
/// Controller-only by design: no DTOs, no services, no interfaces, no
/// repositories. Every action is wrapped in try/catch and reports via Fail().
/// </summary>
[Route("api/delivery")]
[ApiController]
[Authorize(Policy = "BackOffice")]
public class DeliveryController : ApiControllerBase
{
    private readonly PushNotificationService _push;

    public DeliveryController(AppDbContext db, IConfiguration cfg,
        ILogger<DeliveryController> logger, IWebHostEnvironment env,
        PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;

    // ══════════════════════════════════════════════════════════════════
    //  LIST
    // ══════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> GetDeliveries(
        [FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? channel,
        [FromQuery] bool openOnly = false)
    {
        try
        {
            var rows = _db.Deliveries.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(d => d.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(channel)) rows = rows.Where(d => d.Channel.ChannelKey == channel);
            if (openOnly) rows = rows.Where(d => d.Status.IsOpen);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(d => d.DeliveryNo.ToLower().Contains(term) ||
                                       (d.TrackingNo != null && d.TrackingNo.ToLower().Contains(term)) ||
                                       d.Order.CustomerUser.LegalName.ToLower().Contains(term));
            }

            var items = await rows
                .OrderByDescending(d => d.BookedDate).ThenByDescending(d => d.DeliveryId)
                .Select(d => new
                {
                    id = d.DeliveryId,
                    deliveryNo = d.DeliveryNo,
                    orderId = d.OrderId,
                    orderNo = d.Order.OrderNo,
                    invoiceId = d.InvoiceId,
                    invoiceNo = d.Invoice != null ? d.Invoice.InvoiceNo : null,
                    customerId = d.Order.CustomerUserId,
                    customerName = d.Order.CustomerUser.LegalName,
                    customerPhone = d.Order.CustomerUser.User.Phone,
                    destination = d.Order.CustomerUser.City.CityName,
                    channelId = d.ChannelId,
                    channel = d.Channel.ChannelKey,
                    channelName = d.Channel.ChannelName,
                    confirmedByRoleId = d.Channel.ConfirmedByRoleId,
                    confirmedByRole = d.Channel.ConfirmedByRole.RoleKey,
                    remindAfterDays = d.Channel.RemindAfterDays,
                    requiresBilty = d.Channel.RequiresBilty,
                    courierId = d.CourierId,
                    courierName = d.Courier != null ? d.Courier.CourierName : null,
                    trackingNo = d.TrackingNo,
                    trackingUrlTemplate = d.Courier != null ? d.Courier.TrackingUrlTemplate : null,
                    bookedDate = d.BookedDate,
                    expectedDate = d.ExpectedDate,
                    deliveredDate = d.DeliveredDate,
                    status = d.Status.StatusKey,
                    statusName = d.Status.StatusName,
                    isOpen = d.Status.IsOpen,
                    parcels = d.Parcels,
                    weightKg = d.WeightKg,
                    codAmount = d.CodAmount,
                    codSettled = d.IsCodSettled,
                    bookingCharge = d.BookingCharge,
                    remindersSent = d.RemindersSent,
                    confirmedBy = d.ConfirmedByUser != null ? d.ConfirmedByUser.User.FullName : null,
                    notes = d.Notes
                })
                .ToListAsync();

            var today = Today();
            var shaped = items.Select(d => new
            {
                d.id, d.deliveryNo, d.orderId, d.orderNo, d.invoiceId, d.invoiceNo,
                d.customerId, d.customerName,
                customerInitials = Initials(d.customerName),
                d.customerPhone, d.destination,
                d.channelId, d.channel, d.channelName,
                d.confirmedByRoleId, d.confirmedByRole, d.remindAfterDays, d.requiresBilty,
                d.courierId, d.courierName, d.trackingNo, d.trackingUrlTemplate,
                d.bookedDate, d.expectedDate, d.deliveredDate,
                d.status, d.statusName, d.isOpen,
                d.parcels, d.weightKg, d.codAmount, d.codSettled, d.bookingCharge,
                d.remindersSent, d.confirmedBy, d.notes,

                /* Derived, never stored: finish the work and the row stops being
                   overdue on its own. */
                daysInFlight = (d.deliveredDate ?? today).DayNumber - d.bookedDate.DayNumber,
                isOverdue = d.isOpen && d.expectedDate != null && d.expectedDate < today,
                needsReminder = d.isOpen &&
                    today.DayNumber - d.bookedDate.DayNumber >= d.remindAfterDays
            }).ToList();

            return Ok(new
            {
                inFlight = shaped.Count(d => d.isOpen),
                overdue = shaped.Count(d => d.isOverdue),
                pendingCodTotal = shaped.Where(d => !d.codSettled).Sum(d => d.codAmount),
                items = shaped
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the delivery list");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CONFIRMATION  --  owned by the channel
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Confirms a delivery arrived. Refuses unless the signed-in role is the one
    /// the channel names as its confirmer (a Super Admin may always confirm).
    /// This is the server-side half of "the button only shows for the right role".
    /// </summary>
    [HttpPost("{id:int}/confirm")]
    public async Task<IActionResult> ConfirmDelivery(int id, [FromBody] ConfirmDeliveryRequest? body)
    {
        try
        {
            var delivery = await _db.Deliveries
                .Include(d => d.Channel).ThenInclude(c => c.ConfirmedByRole)
                .Include(d => d.Status)
                .FirstOrDefaultAsync(d => d.DeliveryId == id);

            if (delivery is null) return NotFound(new { message = $"No delivery with id {id}." });
            if (delivery.DeliveredDate is not null)
                return BadRequest(new { message = $"{delivery.DeliveryNo} was already confirmed on {delivery.DeliveredDate:yyyy-MM-dd}." });

            var myRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            var owner = delivery.Channel.ConfirmedByRole.RoleKey;

            if (myRole != owner && myRole != "super-admin")
                return StatusCode(403, new
                {
                    message = $"{delivery.Channel.ChannelName} deliveries are confirmed by " +
                              $"{delivery.Channel.ConfirmedByRole.RoleName}, not by you.",
                    requiredRole = owner
                });

            var delivered = await _db.DeliveryStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DELIVERED");
            if (delivered is null) return BadRequest(new { message = "No DELIVERED status is configured." });

            delivery.StatusId = delivered.StatusId;
            delivery.DeliveredDate = body?.DeliveredDate ?? Today();
            delivery.ConfirmedByUserId = CurrentUserId();
            if (!string.IsNullOrWhiteSpace(body?.Notes)) delivery.Notes = body.Notes;

            await _db.SaveChangesAsync();
            await Log("DELIVERY_CONFIRMED", "Delivery", delivery.DeliveryNo,
                $"{delivery.Channel.ChannelName}", 2);

            /* -- A8 -- */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept", "accountant" },
                NotificationKinds.OrderDelivered,
                "Order delivered",
                $"{delivery.DeliveryNo} reached the customer.",
                url: $"/delivery/{delivery.DeliveryId}",
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"{delivery.DeliveryNo} confirmed as delivered." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"confirm delivery {id}");
        }
    }

    /// <summary>Marks the COD cash for a delivery as settled back to the office.</summary>
    [HttpPost("{id:int}/settle-cod")]
    public async Task<IActionResult> SettleCod(int id)
    {
        try
        {
            var delivery = await _db.Deliveries.FirstOrDefaultAsync(d => d.DeliveryId == id);
            if (delivery is null) return NotFound(new { message = $"No delivery with id {id}." });
            if (delivery.CodAmount <= 0)
                return BadRequest(new { message = $"{delivery.DeliveryNo} carries no COD." });
            if (delivery.IsCodSettled)
                return BadRequest(new { message = $"COD on {delivery.DeliveryNo} is already settled." });

            delivery.IsCodSettled = true;
            await _db.SaveChangesAsync();
            await Log("COD_SETTLED", "Delivery", delivery.DeliveryNo, $"{delivery.CodAmount:N2}", 2);

            /* -- A9 -- money arriving. Severe: this is cash the courier was
               holding, and the moment it lands is worth knowing immediately. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "sales" },
                NotificationKinds.CodSettled,
                $"COD received by {CurrentUserName()}",
                $"PKR {delivery.CodAmount:N0} settled on {delivery.DeliveryNo}.",
                url: $"/delivery/{delivery.DeliveryId}",
                severe: true,
                exceptUserId: CurrentUserId());

            return Ok(new { id, message = $"COD on {delivery.DeliveryNo} settled." });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"settle COD on delivery {id}");
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
                        id = c.ChannelId, key = c.ChannelKey, name = c.ChannelName,
                        description = c.Description,
                        confirmedByRole = c.ConfirmedByRole.RoleKey,
                        confirmedByRoleName = c.ConfirmedByRole.RoleName,
                        remindAfterDays = c.RemindAfterDays,
                        remindEveryHours = c.RemindEveryHours,
                        autoConfirm = c.AutoConfirm,
                        requiresBilty = c.RequiresBilty
                    })
                    .ToListAsync(),
                statuses = await _db.DeliveryStatuses.AsNoTracking()
                    .Select(s => new { id = s.StatusId, key = s.StatusKey, name = s.StatusName, isOpen = s.IsOpen })
                    .ToListAsync(),
                couriers = await _db.Couriers.AsNoTracking()
                    .Where(c => c.IsActive).OrderBy(c => c.CourierName)
                    .Select(c => new
                    {
                        id = c.CourierId, name = c.CourierName, shortName = c.ShortName,
                        codSettlementDays = c.CodSettlementDays,
                        bookingCharge = c.BookingCharge,
                        codFeePercent = c.CodFeePercent,
                        trackingUrlTemplate = c.TrackingUrlTemplate
                    })
                    .ToListAsync(),
                channelCarriers = await _db.DeliveryChannels.AsNoTracking()
                    .Where(c => c.IsActive)
                    .Select(c => new
                    {
                        channelId = c.ChannelId,
                        channelKey = c.ChannelKey,
                        carriers = c.Couriers.Select(x => new { id = x.CourierId, name = x.CourierName }).ToList()
                    })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load delivery lookups");
        }
    }

    // ══════════════════════════ request bodies ══════════════════════════

    public record ConfirmDeliveryRequest(DateOnly? DeliveredDate, string? Notes);
}
