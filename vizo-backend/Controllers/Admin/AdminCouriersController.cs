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

    [HttpGet("couriers")]
    public async Task<IActionResult> GetCouriers()
    {
        try
        {
            return Ok(await _db.Couriers
                    .OrderBy(c => c.CourierId)
                    .Select(c => new
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
                        consignmentCount = c.Deliveries.Count
                    })
                    .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/couriers");
        }
    }

    [HttpPost("couriers")]
    public async Task<IActionResult> CreateCourier([FromBody] CourierRequest body)
    {
        try
        {
            var problem = await ValidateCourier(body, null);
            if (problem is not null) return BadRequest(new { message = problem });

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
            _db.Couriers.Add(c);
            await _db.SaveChangesAsync();
            await Log("CREATED", "Courier", c.CourierName, null, 1);
            return Ok(new { id = c.CourierId, message = $"{c.CourierName} added." });
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
            var c = await _db.Couriers.FirstOrDefaultAsync(x => x.CourierId == id);
            if (c is null) return NotFound(new { message = "Courier not found." });

            var problem = await ValidateCourier(body, id);
            if (problem is not null) return BadRequest(new { message = problem });

            c.CourierName = body.Name.Trim();
            c.ShortName = body.ShortName.Trim();
            c.ContactPerson = body.ContactPerson?.Trim();
            c.Phone = body.Phone?.Trim();
            c.CodSettlementDays = (short)body.CodSettlementDays;
            c.BookingCharge = body.BookingCharge;
            c.CodFeePercent = body.CodFeePercent;
            c.TrackingUrlTemplate = body.TrackingUrlTemplate?.Trim();
            c.IsActive = body.IsActive;

            await _db.SaveChangesAsync();
            await Log("UPDATED", "Courier", c.CourierName, null, 1);
            return Ok(new { message = $"{c.CourierName} updated." });
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
            var c = await _db.Couriers.FirstOrDefaultAsync(x => x.CourierId == id);
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

    // ══════════════════════ request bodies ══════════════════════

    public record CourierRequest(
        string Name, string ShortName, string? ContactPerson, string? Phone,
        int CodSettlementDays, decimal BookingCharge, decimal CodFeePercent,
        string? TrackingUrlTemplate, bool IsActive);
}