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
            /* WHO MAY DO WHAT, decided once and sent with every row so the screen
               never guesses (27 Sep, round E):
                 · the ORDER DESK sees no money -- no COD figure, no booking
                   charge, no pending-COD total (the owner's rule of 26 Sep; the
                   same one HideMoneyFromOrderDesk applies to orders). It is told
                   only WHETHER cash is to be collected at the door;
                 · COD is settled by the Super Admin or the accountant alone;
                 · a delivery is confirmed by the role its channel names, or the
                   Super Admin (ConfirmDelivery enforces it; canConfirm mirrors it).
               Locals, not CurrentRole() inside a query (HANDOFF trap 27). */
            var role = CurrentRole();
            var noMoney = role == OrderWorkflow.RoleOrderDept;
            var mayHandleCod = role is OrderWorkflow.RoleAdmin or OrderWorkflow.RoleAccountant;

            var rows = _db.Deliveries.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(d => d.Status.StatusKey == status);
            if (!string.IsNullOrWhiteSpace(channel)) rows = rows.Where(d => d.Channel.ChannelKey == channel);
            if (openOnly) rows = rows.Where(d => d.Status.IsOpen);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(d => d.DeliveryNo.ToLower().Contains(term) ||
                                       (d.TrackingNo != null && d.TrackingNo.ToLower().Contains(term)) ||
                                       (d.Order.CustomerUser.DisplayName ?? d.Order.CustomerUser.LegalName).ToLower().Contains(term));
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
                    customerName = (d.Order.CustomerUser.DisplayName ?? d.Order.CustomerUser.LegalName),
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
                    receivedBy = d.ReceivedBy,
                    confirmedAt = d.ConfirmedAt,
                    codSettledOn = d.CodSettledOn,
                    codFee = d.CodFeeAmount,
                    codReceiptNo = _db.Collections.Where(c => c.CollectionId == d.CodCollectionId)
                        .Select(c => c.ReceiptNo).FirstOrDefault(),
                    codVoucherNo = _db.Collections.Where(c => c.CollectionId == d.CodCollectionId && c.Voucher != null)
                        .Select(c => c.Voucher!.VoucherNo).FirstOrDefault(),
                    salesPerson = d.Order.SalesPersonUser != null ? d.Order.SalesPersonUser.User.FullName : null,
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
                d.parcels, d.weightKg,
                codAmount = noMoney ? 0m : d.codAmount,
                /* Whether cash is to be taken at the door -- the one COD fact the
                   order desk needs to do its job, without the figure. */
                collectsCash = d.codAmount > 0,
                d.codSettled,
                bookingCharge = noMoney ? 0m : d.bookingCharge,
                codFee = noMoney ? 0m : d.codFee,
                codSettledOn = noMoney ? null : d.codSettledOn,
                codReceiptNo = noMoney ? null : d.codReceiptNo,
                codVoucherNo = noMoney ? null : d.codVoucherNo,
                d.remindersSent, d.confirmedBy, d.receivedBy, d.confirmedAt, d.salesPerson, d.notes,

                /* Derived, never stored: finish the work and the row stops being
                   overdue on its own. */
                daysInFlight = (d.deliveredDate ?? today).DayNumber - d.bookedDate.DayNumber,
                isOverdue = d.isOpen && d.expectedDate != null && d.expectedDate < today,
                needsReminder = d.isOpen &&
                    today.DayNumber - d.bookedDate.DayNumber >= d.remindAfterDays,

                /* The two buttons. Confirming: not yet delivered, not come back,
                   and the caller owns the channel (or is the Super Admin).
                   Settling: delivered first -- a courier pays over what it has
                   collected, and it has collected nothing until the goods are
                   handed over -- carrying COD, not settled, back office only. */
                canConfirm = d.deliveredDate == null && d.status != "RETURNED_TO_SENDER" &&
                             (role == d.confirmedByRole || role == OrderWorkflow.RoleAdmin),
                canSettleCod = mayHandleCod && d.deliveredDate != null && d.codAmount > 0 && !d.codSettled
            }).ToList();

            /* THE CARDS, counted HERE and not in the browser. "Delivered this
               period" used to be the number of DELIVERED rows the screen happened
               to have loaded -- every delivery ever made, labelled "this period".
               It is now this calendar month by DeliveredDate, over the whole
               table whatever filter the list is showing. */
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var cards = await _db.Deliveries.AsNoTracking()
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    deliveredThisMonth = g.Count(d => d.DeliveredDate != null && d.DeliveredDate >= monthStart && d.DeliveredDate <= today),
                    needAttention = g.Count(d => d.Status.StatusKey == "FAILED" || d.Status.StatusKey == "RETURNED_TO_SENDER"),
                    awaitingSettlement = g.Count(d => d.DeliveredDate != null && d.CodAmount > 0 && !d.IsCodSettled),
                    pendingCod = g.Where(d => d.CodAmount > 0 && !d.IsCodSettled).Sum(d => (decimal?)d.CodAmount) ?? 0m
                })
                .FirstOrDefaultAsync();

            return Ok(new
            {
                inFlight = shaped.Count(d => d.isOpen),
                overdue = shaped.Count(d => d.isOverdue),
                pendingCodTotal = noMoney ? 0m : cards?.pendingCod ?? 0m,
                deliveredThisMonth = cards?.deliveredThisMonth ?? 0,
                periodLabel = today.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                needAttention = cards?.needAttention ?? 0,
                awaitingSettlement = noMoney ? 0 : cards?.awaitingSettlement ?? 0,
                moneyHidden = noMoney,
                mayHandleCod,
                mayBook = role is OrderWorkflow.RoleAdmin or OrderWorkflow.RoleOrderDept,
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
    ///
    /// NO SCREEN CALLED THIS until 27 Sep (round E): the endpoint existed and
    /// the Delivery page had no button for it. It now also asks the two things
    /// anybody chasing a missing parcel wants -- WHEN it arrived and WHO signed
    /// for it -- and moves the ORDER to Delivered with it, so the order screen
    /// and the delivery screen stop disagreeing about the same parcel.
    /// </summary>
    [HttpPost("{id:int}/confirm")]
    public async Task<IActionResult> ConfirmDelivery(int id, [FromBody] ConfirmDeliveryRequest? body)
    {
        try
        {
            var delivery = await _db.Deliveries
                .Include(d => d.Channel).ThenInclude(c => c.ConfirmedByRole)
                .Include(d => d.Status)
                .Include(d => d.Order).ThenInclude(o => o.Status)
                .FirstOrDefaultAsync(d => d.DeliveryId == id);

            if (delivery is null) return NotFound(new { message = $"No delivery with id {id}." });
            if (delivery.DeliveredDate is not null)
                return BadRequest(new { message = $"{delivery.DeliveryNo} was already confirmed on {delivery.DeliveredDate:yyyy-MM-dd}." });
            if (delivery.Status.StatusKey == "RETURNED_TO_SENDER")
                return BadRequest(new { message = $"{delivery.DeliveryNo} came back to us; it cannot be marked delivered." });

            var myRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            var owner = delivery.Channel.ConfirmedByRole.RoleKey;

            if (myRole != owner && myRole != "super-admin")
                return StatusCode(403, new
                {
                    message = $"{delivery.Channel.ChannelName} deliveries are confirmed by " +
                              $"{delivery.Channel.ConfirmedByRole.RoleName}, not by you.",
                    requiredRole = owner
                });

            /* The day it arrived: not in the future, and not before it left. */
            var date = body?.DeliveredDate ?? Today();
            if (date > Today())
                return BadRequest(new { message = "A delivery cannot be confirmed for a day that has not come yet." });
            if (date < delivery.BookedDate)
                return BadRequest(new { message = $"{delivery.DeliveryNo} was booked on {delivery.BookedDate:dd MMM yyyy}; it cannot have arrived before that." });

            var receivedBy = Clean(body?.ReceivedBy, 100);
            if (receivedBy is null)
                return BadRequest(new { message = "Say who received it at the shop -- the name on the signature." });

            var delivered = await _db.DeliveryStatuses.FirstOrDefaultAsync(s => s.StatusKey == "DELIVERED");
            if (delivered is null) return BadRequest(new { message = "No DELIVERED status is configured." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            delivery.StatusId = delivered.StatusId;
            delivery.DeliveredDate = date;
            delivery.ReceivedBy = receivedBy;
            delivery.ConfirmedAt = Now();
            /* ConfirmedByUserId is a foreign key to "Employee", not "User" (HANDOFF
               trap 2). It used to be written straight from the token, which throws
               for anybody without an Employee row. */
            delivery.ConfirmedByUserId = await CurrentEmployeeId();
            var note = Clean(body?.Notes, 500);
            if (note is not null)
                delivery.Notes = string.IsNullOrWhiteSpace(delivery.Notes) ? note : Clean($"{delivery.Notes} | {note}", 500);

            /* The order follows its parcel: Dispatched -> Delivered, the last step
               of the chain, logged the same way the order screen logs a move
               (ORDER_STATUS_CHANGED, "from -> to") so the order's history reads it.
               Only from DISPATCHED -- an order somebody has already moved on, or
               back, is left as they left it. */
            var orderMoved = false;
            if (delivery.Order.Status.StatusKey == OrderWorkflow.Dispatched)
            {
                var orderDelivered = await _db.OrderStatuses.FirstOrDefaultAsync(s => s.StatusKey == OrderWorkflow.Delivered);
                if (orderDelivered is not null)
                {
                    delivery.Order.StatusId = orderDelivered.StatusId;
                    orderMoved = true;
                }
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            await Log("DELIVERY_CONFIRMED", "Delivery", delivery.DeliveryNo,
                $"{delivery.Channel.ChannelName}; delivered {date:yyyy-MM-dd}; received by {receivedBy}", 2);
            if (orderMoved)
                await Log("ORDER_STATUS_CHANGED", "SalesOrder", delivery.Order.OrderNo,
                    $"Dispatched -> Delivered. Confirmed on {delivery.DeliveryNo}, received by {receivedBy}.", 1);

            /* -- A8 -- and the rep whose customer it is: it is his relationship. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "order-dept", "accountant" },
                NotificationKinds.OrderDelivered,
                $"Order delivered, confirmed by {CurrentUserName()}",
                $"{delivery.DeliveryNo} ({delivery.Order.OrderNo}) reached the customer on {date:dd MMM}; received by {receivedBy}.",
                url: $"/delivery/{delivery.DeliveryId}",
                exceptUserId: CurrentUserId(),
                alsoUserIds: delivery.Order.SalesPersonUserId is null
                    ? null : new[] { delivery.Order.SalesPersonUserId.Value });

            return Ok(new
            {
                id,
                orderMoved,
                message = $"{delivery.DeliveryNo} confirmed as delivered on {date:dd MMM yyyy}, received by {receivedBy}." +
                          (orderMoved ? $" {delivery.Order.OrderNo} is now Delivered." : "")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"confirm delivery {id}");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  COD SETTLEMENT  --  money, so it goes through the books
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The courier has paid over the cash it collected at the door.
    ///
    /// WHAT THIS USED TO DO: flip IsCodSettled and send a notification. The
    /// money never reached the books -- the bank did not show it, the customer
    /// still owed it in his ledger, and his invoice still read unpaid. A button
    /// labelled "settled" that changes no balance is worse than no button.
    ///
    /// WHAT IT DOES NOW, in one transaction:
    ///   1. a CONFIRMED collection for the customer (series COL), allocated to
    ///      the order -- exactly what Confirm Collections writes, so the order,
    ///      the invoice and the customer's statement all move the same way;
    ///   2. LedgerPosting.PostCollectionAsync: a receipt voucher, Dr the cash or
    ///      bank account the money landed in / Cr 1130 Accounts Receivable (the
    ///      customer), for the FULL COD -- the customer paid all of it, to the
    ///      courier;
    ///   3. the courier's fee, if it kept one: Dr 5114 Delivery &amp; Courier /
    ///      Cr the same bank. So the bank ends up holding what actually arrived
    ///      (COD - fee), the customer is credited with everything he paid, and
    ///      the fee is an expense. Two entries, each balanced on its own.
    /// Any refusal inside (a closed month, a missing account) rolls all of it back.
    /// </summary>
    [HttpPost("{id:int}/settle-cod")]
    /* Settling COD is taking money in -- a collection by another name, and the
       owner's rule of 26 September is that the order desk handles none. */
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SettleCod(int id, [FromBody] SettleCodRequest? body)
    {
        try
        {
            var delivery = await _db.Deliveries
                .Include(d => d.Order).ThenInclude(o => o.Status)
                .Include(d => d.Order).ThenInclude(o => o.CustomerUser)
                .FirstOrDefaultAsync(d => d.DeliveryId == id);
            if (delivery is null) return NotFound(new { message = $"No delivery with id {id}." });
            if (delivery.CodAmount <= 0)
                return BadRequest(new { message = $"{delivery.DeliveryNo} carries no COD." });
            if (delivery.IsCodSettled)
                return BadRequest(new { message = $"COD on {delivery.DeliveryNo} is already settled." });
            if (delivery.DeliveredDate is null)
                return BadRequest(new { message = $"{delivery.DeliveryNo} has not been delivered yet -- the courier has collected nothing to pay over. Mark it delivered first." });
            if (body is null) return BadRequest(new { message = "Say how the courier's money came in." });

            var order = delivery.Order;
            var customerName = order.CustomerUser.DisplayName ?? order.CustomerUser.LegalName;

            /* What the order still owes, worked out the way Confirm Collections
               does: its invoice, less confirmed collections allocated to it, less
               any posted receipt voucher on the invoice that is not a
               collection's own (so nothing is counted twice). */
            var owing = await _db.SalesOrders.AsNoTracking()
                .Where(o => o.OrderId == order.OrderId && o.SalesInvoice != null)
                .Select(o => new
                {
                    invoiceNo = o.SalesInvoice!.InvoiceNo,
                    total = o.SalesInvoice!.TotalAmount,
                    collected = o.CollectionAllocations
                        .Where(a => a.Collection.Status.StatusKey == "CONFIRMED")
                        .Sum(a => (decimal?)a.Amount) ?? 0m,
                    viaVouchers = o.SalesInvoice!.VoucherAllocations
                        .Where(v => v.Voucher.Status.StatusKey == "POSTED" && !v.Voucher.Collections.Any())
                        .Sum(v => (decimal?)v.Amount) ?? 0m
                })
                .FirstOrDefaultAsync();
            if (owing is null)
                return BadRequest(new { message = $"{order.OrderNo} has no invoice, so the customer's account has nothing to settle against. Raise the invoice first." });

            var balance = owing.total - owing.collected - owing.viaVouchers;
            if (delivery.CodAmount > balance)
                return BadRequest(new
                {
                    message = balance <= 0
                        ? $"{order.OrderNo} is already paid in full, so this COD would pay it twice. Check Confirm Collections before settling."
                        : $"{order.OrderNo} only owes {balance:N2}, less than the COD of {delivery.CodAmount:N2} -- part of it was recorded another way. Settle the rest on Confirm Collections instead."
                });

            var fee = Math.Round(body.CourierFee ?? 0m, 2, MidpointRounding.AwayFromZero);
            if (fee < 0) return BadRequest(new { message = "The courier's fee cannot be negative." });
            if (fee >= delivery.CodAmount)
                return BadRequest(new { message = "The courier's fee must be less than the COD it collected." });

            var method = await _db.PaymentMethods.AsNoTracking()
                .FirstOrDefaultAsync(m => m.MethodId == body.MethodId && m.IsActive);
            var cashCode = LedgerPosting.CashAccountCodeFor(method?.MethodKey);
            if (method is null || cashCode is null || method.MethodKey == "PETTY_CASH")
                return BadRequest(new { message = "Pick where the courier's money landed -- cash or a bank account." });
            if (method.MethodKey != "CASH" && string.IsNullOrWhiteSpace(body.ReferenceNo))
                return BadRequest(new { message = $"{method.MethodName} needs its reference (the courier's payment or transfer number)." });

            var date = body.SettledOn ?? Today();
            if (date > Today()) return BadRequest(new { message = "Money cannot arrive on a day that has not come yet." });
            if (date < delivery.DeliveredDate)
                return BadRequest(new { message = $"{delivery.DeliveryNo} was delivered on {delivery.DeliveredDate:dd MMM yyyy}; the courier cannot have paid before that." });

            var me = await CurrentEmployeeId();
            if (me is null)
                return BadRequest(new { message = "Your sign-in has no staff record, so it cannot be named as the one who received this money." });

            var confirmed = await _db.CollectionStatuses.FirstAsync(s => s.StatusKey == "CONFIRMED");
            var feeAccount = await LedgerPosting.AccountIdAsync(_db, CourierExpenseCode);
            var cashAccount = await LedgerPosting.AccountIdAsync(_db, cashCode);
            if (fee > 0 && (feeAccount is null || cashAccount is null))
                return BadRequest(new { message = $"Accounts {CourierExpenseCode} and {cashCode} must both be in the chart before a courier's fee can post." });

            await using var tx = await _db.Database.BeginTransactionAsync();

            var c = new Collection
            {
                ReceiptNo = await NextNumber("COL"),
                CustomerUserId = order.CustomerUserId,
                CollectedByUserId = me.Value,
                CollectedOn = date,
                Amount = delivery.CodAmount,
                MethodId = method.MethodId,
                ReferenceNo = Clean(body.ReferenceNo, 50),
                StatusId = confirmed.StatusId,
                ConfirmedOn = Today(),
                ConfirmedByUserId = me,
                Note = Clean($"COD on {delivery.DeliveryNo}" +
                             (fee > 0 ? $", courier kept {fee:N2}" : "") +
                             (string.IsNullOrWhiteSpace(body.Note) ? "" : $". {body.Note.Trim()}"), 300)
            };
            _db.Collections.Add(c);
            await _db.SaveChangesAsync();
            _db.CollectionAllocations.Add(new CollectionAllocation
            {
                CollectionId = c.CollectionId, OrderId = order.OrderId, Amount = delivery.CodAmount
            });
            await _db.SaveChangesAsync();

            var error = await LedgerPosting.PostCollectionAsync(_db, c.CollectionId, CurrentUserId());
            if (error is not null) return BadRequest(new { message = error });

            int? feeEntryId = null;
            string? feeEntryNo = null;
            if (fee > 0)
            {
                var locationId = await _db.Locations.AsNoTracking()
                    .Where(l => l.IsActive).OrderByDescending(l => l.IsDefault).ThenBy(l => l.LocationId)
                    .Select(l => l.LocationId).FirstAsync();
                var (entry, feeError) = await LedgerPosting.WriteEntryAsync(_db, date, "PAYMENT", locationId,
                    delivery.DeliveryNo, $"Courier's fee kept out of COD on {delivery.DeliveryNo} ({order.OrderNo})",
                    CurrentUserId(), new[]
                    {
                        new LedgerPosting.Leg(feeAccount!.Value, fee, 0m, $"COD fee, {delivery.DeliveryNo}"),
                        new LedgerPosting.Leg(cashAccount!.Value, 0m, fee, $"Kept by the courier, {delivery.DeliveryNo}")
                    });
                if (entry is null) return BadRequest(new { message = $"The courier's fee could not post: {feeError}" });
                feeEntryId = entry.EntryId;
                feeEntryNo = entry.EntryNo;
            }

            delivery.IsCodSettled = true;
            delivery.CodCollectionId = c.CollectionId;
            delivery.CodSettledOn = date;
            delivery.CodFeeAmount = fee;
            delivery.CodFeeEntryId = feeEntryId;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            var voucherNo = await _db.Vouchers.AsNoTracking().Where(v => v.VoucherId == c.VoucherId)
                .Select(v => v.VoucherNo).FirstOrDefaultAsync();
            var left = balance - delivery.CodAmount;

            await Log("COD_SETTLED", "Delivery", delivery.DeliveryNo,
                $"{delivery.CodAmount:N2} via {method.MethodKey} -> {c.ReceiptNo} / {voucherNo}" +
                (fee > 0 ? $"; courier fee {fee:N2} -> {feeEntryNo}" : ""), 2);

            /* -- A9 -- money arriving. Severe: this is cash the courier was
               holding, and the moment it lands is worth knowing immediately.
               It used to go to EVERY sales rep; it goes to the one whose
               customer this is. */
            await _push.NotifyRolesAsync(
                new[] { "super-admin", "accountant" },
                NotificationKinds.CodSettled,
                $"COD received by {CurrentUserName()}",
                $"PKR {delivery.CodAmount:N0} from {customerName} on {delivery.DeliveryNo} ({order.OrderNo})" +
                (fee > 0 ? $", courier kept PKR {fee:N0}." : ".") +
                (left <= 0 ? " Paid in full." : $" PKR {left:N0} still owed."),
                url: $"/delivery/{delivery.DeliveryId}",
                severe: true,
                exceptUserId: CurrentUserId(),
                alsoUserIds: order.SalesPersonUserId is null ? null : new[] { order.SalesPersonUserId.Value });

            return Ok(new
            {
                id,
                receiptNo = c.ReceiptNo,
                voucherNo,
                feeEntryNo,
                balance = Math.Max(0, left),
                message = $"COD on {delivery.DeliveryNo} settled: {c.ReceiptNo} posted as {voucherNo}" +
                          (fee > 0 ? $", courier's fee of PKR {fee:N0} posted as {feeEntryNo}." : ".") +
                          (left <= 0 ? $" {order.OrderNo} is paid in full." : $" PKR {left:N0} is still owed on {order.OrderNo}.")
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, $"settle COD on delivery {id}");
        }
    }

    /// <summary>5114 Delivery &amp; Courier -- where a courier's COD fee is expensed.</summary>
    private const string CourierExpenseCode = "5114";

    /// <summary>
    /// Where the courier's money can land: every active method that maps to a
    /// real cash or bank account, named with that account. Same list Confirm
    /// Collections offers, for the same reason -- the accountant should see
    /// where the money will show before pressing the button.
    /// </summary>
    [HttpGet("settle-methods")]
    [Authorize(Roles = "super-admin,accountant")]
    public async Task<IActionResult> SettleMethods()
    {
        try
        {
            var methods = await _db.PaymentMethods.AsNoTracking().Where(m => m.IsActive)
                .OrderBy(m => m.MethodId).Select(m => new { m.MethodId, m.MethodKey, m.MethodName }).ToListAsync();
            var result = new List<object>();
            foreach (var m in methods)
            {
                var code = LedgerPosting.CashAccountCodeFor(m.MethodKey);
                if (code is null || m.MethodKey == "PETTY_CASH") continue;
                var acc = await _db.Accounts.AsNoTracking().Where(a => a.AccountCode == code && !a.IsGroup && a.IsActive)
                    .Select(a => new { a.AccountCode, a.AccountName }).FirstOrDefaultAsync();
                if (acc is null) continue;
                result.Add(new
                {
                    id = m.MethodId, key = m.MethodKey, name = m.MethodName,
                    account = $"{acc.AccountCode} {acc.AccountName}",
                    needsReference = m.MethodKey != "CASH"
                });
            }

            var fee = await _db.Accounts.AsNoTracking().Where(a => a.AccountCode == CourierExpenseCode)
                .Select(a => a.AccountCode + " " + a.AccountName).FirstOrDefaultAsync();
            return Ok(new { methods = result, feeAccount = fee });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the ways COD can be settled");
        }
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
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

    public record ConfirmDeliveryRequest(DateOnly? DeliveredDate, string? ReceivedBy, string? Notes);

    public record SettleCodRequest(int MethodId, DateOnly? SettledOn, string? ReferenceNo, decimal? CourierFee, string? Note);
}
