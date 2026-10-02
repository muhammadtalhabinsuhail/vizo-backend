using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Courier companies and their COD terms.
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
public class AdminCouriersController : AdminControllerBase
{
    public AdminCouriersController(AppDbContext db, IConfiguration cfg, ILogger<AdminCouriersController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  COURIERS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every courier with the parent category (delivery channel) it sits under.
    ///
    /// WHY a channel per courier: the dispatch form lists, for the channel the
    /// desk picks, only the couriers linked to that channel through the
    /// ChannelCarrier join table. Until Setup could set that link the owner saw
    /// "all couriers together" here and had no way to say TCS belongs under
    /// Online and Daewoo under Local cargo. The schema is many-to-many, but the
    /// business rule is one parent per courier, so the screen works with a
    /// single channelId. channelIds still carries every link that exists today
    /// so a courier wired to several channels (older seed data) can be flagged
    /// and fixed by opening it and saving once.
    ///
    /// Loaded in two steps (rows, then shaped in memory) so the "first channel
    /// by id" pick does not depend on how a given EF version translates an
    /// ordered FirstOrDefault inside a collection projection.
    /// </summary>
    [HttpGet("couriers")]
    public async Task<IActionResult> GetCouriers()
    {
        try
        {
            var rows = await _db.Couriers.AsNoTracking()
                .OrderByDescending(c => c.CourierId)
                .Select(c => new
                {
                    c.CourierId,
                    c.CourierName,
                    c.ShortName,
                    c.ContactPerson,
                    c.Phone,
                    c.CodSettlementDays,
                    c.BookingCharge,
                    c.CodFeePercent,
                    c.TrackingUrlTemplate,
                    c.IsActive,
                    ConsignmentCount = c.Deliveries.Count,
                    Channels = c.Channels
                        .Select(ch => new { ch.ChannelId, ch.ChannelName })
                        .ToList()
                })
                .ToListAsync();

            return Ok(rows.Select(c =>
            {
                var links = c.Channels.OrderBy(ch => ch.ChannelId).ToList();
                var first = links.FirstOrDefault();
                return new
                {
                    id = c.CourierId,
                    name = c.CourierName,
                    shortName = c.ShortName,
                    contactPerson = c.ContactPerson,
                    phone = c.Phone,
                    codSettlementDays = c.CodSettlementDays,
                    bookingCharge = c.BookingCharge,
                    codFeePercent = c.CodFeePercent,
                    trackingUrlTemplate = c.TrackingUrlTemplate,
                    isActive = c.IsActive,
                    consignmentCount = c.ConsignmentCount,
                    /* null when the courier is under no channel yet -- the
                       dispatch form can never offer such a courier. */
                    channelId = first?.ChannelId,
                    channelName = first?.ChannelName,
                    channelIds = links.Select(ch => ch.ChannelId).ToList()
                };
            }).ToList());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/couriers");
        }
    }

    /// <summary>
    /// The parent categories a courier can sit under (Karachi own team, Online
    /// courier, Local cargo, Heavy logistics). Inactive channels are returned
    /// too, flagged, so the screen can still label a courier that sits under a
    /// retired channel; the form only offers the active ones and the save
    /// refuses an inactive one. A separate route keeps GET /couriers a plain
    /// array, which is the shape it has always had.
    /// </summary>
    [HttpGet("couriers/channels")]
    public async Task<IActionResult> GetCourierChannels()
    {
        try
        {
            return Ok(await _db.DeliveryChannels.AsNoTracking()
                .OrderBy(c => c.ChannelId)
                .Select(c => new
                {
                    id = c.ChannelId,
                    key = c.ChannelKey,
                    name = c.ChannelName,
                    description = c.Description,
                    isActive = c.IsActive
                })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/couriers/channels");
        }
    }

    [HttpPost("couriers")]
    public async Task<IActionResult> CreateCourier([FromBody] CourierRequest body)
    {
        try
        {
            var problem = await ValidateCourier(body, null);
            if (problem is not null) return BadRequest(new { message = problem });

            var (channel, channelProblem) = await FindChannel(body.ChannelId);
            if (channel is null) return BadRequest(new { message = channelProblem });

            var c = new Courier
            {
                CourierName = body.Name.Trim(),
                ShortName = body.ShortName.Trim(),
                ContactPerson = body.ContactPerson?.Trim(),
                Phone = body.Phone?.Trim(),
                CodSettlementDays = (short)body.CodSettlementDays,
                BookingCharge = body.BookingCharge,
                CodFeePercent = body.CodFeePercent,
                TrackingUrlTemplate = body.TrackingUrlTemplate?.Trim(),
                IsActive = body.IsActive
            };
            /* Adding the tracked channel to the skip navigation makes EF insert
               the ChannelCarrier row in the same SaveChanges as the courier. */
            c.Channels.Add(channel);
            _db.Couriers.Add(c);
            await _db.SaveChangesAsync();
            await Log("CREATED", "Courier", c.CourierName, $"Under {channel.ChannelName}", 1);
            return Ok(new { id = c.CourierId, message = $"{c.CourierName} added under {channel.ChannelName}." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/couriers");
        }
    }

    [HttpPut("couriers/{id:int}")]
    public async Task<IActionResult> UpdateCourier(int id, [FromBody] CourierRequest body)
    {
        try
        {
            /* Include the links: clearing a skip navigation only deletes the
               ChannelCarrier rows EF knows about, so they must be loaded. */
            var c = await _db.Couriers
                .Include(x => x.Channels)
                .FirstOrDefaultAsync(x => x.CourierId == id);
            if (c is null) return NotFound(new { message = "Courier not found." });

            var problem = await ValidateCourier(body, id);
            if (problem is not null) return BadRequest(new { message = problem });

            var (channel, channelProblem) = await FindChannel(body.ChannelId);
            if (channel is null) return BadRequest(new { message = channelProblem });

            c.CourierName = body.Name.Trim();
            c.ShortName = body.ShortName.Trim();
            c.ContactPerson = body.ContactPerson?.Trim();
            c.Phone = body.Phone?.Trim();
            c.CodSettlementDays = (short)body.CodSettlementDays;
            c.BookingCharge = body.BookingCharge;
            c.CodFeePercent = body.CodFeePercent;
            c.TrackingUrlTemplate = body.TrackingUrlTemplate?.Trim();
            c.IsActive = body.IsActive;

            /* One parent per courier: replace whatever links exist (none, one,
               or several from older data) with exactly the chosen channel. When
               it is already the only link, leave the collection alone so EF
               issues no delete-and-reinsert of the same key. */
            var alreadyOnlyThat = c.Channels.Count == 1 && c.Channels.First().ChannelId == channel.ChannelId;
            if (!alreadyOnlyThat)
            {
                c.Channels.Clear();
                c.Channels.Add(channel);
            }

            await _db.SaveChangesAsync();
            await Log("UPDATED", "Courier", c.CourierName, $"Under {channel.ChannelName}", 1);
            return Ok(new { message = $"{c.CourierName} updated (under {channel.ChannelName})." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/couriers/{id:int}");
        }
    }

    [HttpDelete("couriers/{id:int}")]
    public async Task<IActionResult> DeleteCourier(int id)
    {
        try
        {
            /* Links loaded so a hard delete also removes the courier's
               ChannelCarrier rows instead of tripping fk_cc_courier. */
            var c = await _db.Couriers
                .Include(x => x.Channels)
                .FirstOrDefaultAsync(x => x.CourierId == id);
            if (c is null) return NotFound(new { message = "Courier not found." });

            /* Past consignments keep pointing here, so retire rather than delete. */
            var used = await _db.Deliveries.AnyAsync(d => d.CourierId == id);
            if (used)
            {
                c.IsActive = false;
                await _db.SaveChangesAsync();
                await Log("UPDATED", "Courier", c.CourierName, "Retired - has past deliveries", 3);
                return Ok(new { message = $"{c.CourierName} retired. Past deliveries still show it." });
            }

            c.Channels.Clear();
            _db.Couriers.Remove(c);
            await _db.SaveChangesAsync();
            await Log("DELETED", "Courier", c.CourierName, null, 4);
            return Ok(new { message = $"{c.CourierName} deleted." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete /api/admin/couriers/{id:int}");
        }
    }

    // ════════════════════ validation helpers ════════════════════

    private async Task<string?> ValidateCourier(CourierRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length < 2) return "Courier name is required.";
        if (string.IsNullOrWhiteSpace(b.ShortName)) return "Short name is required.";
        if (b.CodSettlementDays is < 0 or > 60) return "Settlement days must be between 0 and 60.";
        if (b.BookingCharge < 0) return "Booking charge cannot be negative.";
        if (b.CodFeePercent is < 0 or > 20) return "COD fee must be between 0 and 20 percent.";

        var name = b.Name.Trim().ToLower();
        if (await _db.Couriers.AnyAsync(c => c.CourierName.ToLower() == name && c.CourierId != existingId))
            return "Another courier already uses that name.";
        return null;
    }

    /// <summary>
    /// The parent category a save puts the courier under. Required: a courier
    /// under no channel never appears on the dispatch form, which is exactly
    /// the "all together, nothing to pick" problem this field exists to fix.
    /// Returned tracked, because it is added to Courier.Channels.
    /// </summary>
    private async Task<(DeliveryChannel? channel, string? problem)> FindChannel(int? channelId)
    {
        if (channelId is null or <= 0)
            return (null, "Pick the parent category (Karachi own team, Online, Local cargo or Heavy logistics) this courier belongs under.");

        var channel = await _db.DeliveryChannels.FirstOrDefaultAsync(ch => ch.ChannelId == channelId);
        if (channel is null) return (null, "That parent category no longer exists. Reload the page and pick again.");
        if (!channel.IsActive) return (null, $"{channel.ChannelName} is switched off. Pick an active category.");
        return (channel, null);
    }

    // ══════════════════════ request bodies ══════════════════════

    public record CourierRequest(
        string Name, string ShortName, string? ContactPerson, string? Phone,
        int CodSettlementDays, decimal BookingCharge, decimal CodFeePercent,
        string? TrackingUrlTemplate, bool IsActive,
        /* Nullable so a body without it binds and gets the clear message from
           FindChannel instead of a model-binding 400. */
        int? ChannelId = null);
}