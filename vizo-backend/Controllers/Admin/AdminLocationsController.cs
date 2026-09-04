using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Warehouses and shops, and the single-default-location invariant.
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
public class AdminLocationsController : AdminControllerBase
{
    public AdminLocationsController(AppDbContext db, IConfiguration cfg, ILogger<AdminLocationsController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  LOCATIONS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("locations")]
    public async Task<IActionResult> GetLocations([FromQuery] bool includeInactive = true)
    {
        try
        {
            return Ok(await _db.Locations
                    .Where(l => includeInactive || l.IsActive)
                    .OrderBy(l => l.LocationId)
                    .Select(l => new
                    {
                        id = l.LocationId,
                        code = l.LocationCode,
                        name = l.LocationName,
                        kindId = l.KindId,
                        kind = l.Kind.KindKey,
                        kindLabel = l.Kind.KindName,
                        cityId = l.CityId,
                        city = l.City.CityName,
                        address = l.AddressLine,
                        inChargeUserId = l.InChargeUserId,
                        inCharge = l.InChargeUser != null ? l.InChargeUser.FullName : null,
                        isActive = l.IsActive,
                        isDefault = l.IsDefault,
                        excludeFromSellable = l.ExcludeFromSellable,
                        stockUnits = l.StockBalances.Sum(s => (int?)s.Quantity) ?? 0
                    })
                    .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/locations");
        }
    }

    [HttpPost("locations")]
    public async Task<IActionResult> CreateLocation([FromBody] LocationRequest body)
    {
        try
        {
            var problem = await ValidateLocation(body, null);
            if (problem is not null) return BadRequest(new { message = problem });

            var loc = new Location
            {
                LocationCode = body.Code.Trim().ToUpperInvariant(),
                LocationName = body.Name.Trim(),
                KindId = body.KindId,
                CityId = body.CityId,
                AddressLine = body.Address?.Trim() ?? "",
                InChargeUserId = body.InChargeUserId,
                IsActive = body.IsActive,
                IsDefault = body.IsDefault,
                ExcludeFromSellable = body.ExcludeFromSellable
            };

            if (body.IsDefault) await ClearOtherDefaults(null);

            _db.Locations.Add(loc);
            await _db.SaveChangesAsync();
            await Log("CREATED", "Location", loc.LocationCode, loc.LocationName, 1);
            return Ok(new { id = loc.LocationId, message = $"{loc.LocationName} added." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/locations");
        }
    }

    [HttpPut("locations/{id:int}")]
    public async Task<IActionResult> UpdateLocation(int id, [FromBody] LocationRequest body)
    {
        try
        {
            var loc = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == id);
            if (loc is null) return NotFound(new { message = "Location not found." });

            var problem = await ValidateLocation(body, id);
            if (problem is not null) return BadRequest(new { message = problem });

            if (body.IsDefault && !loc.IsDefault) await ClearOtherDefaults(id);

            loc.LocationCode = body.Code.Trim().ToUpperInvariant();
            loc.LocationName = body.Name.Trim();
            loc.KindId = body.KindId;
            loc.CityId = body.CityId;
            loc.AddressLine = body.Address?.Trim() ?? "";
            loc.InChargeUserId = body.InChargeUserId;
            loc.IsActive = body.IsActive;
            loc.IsDefault = body.IsDefault;
            loc.ExcludeFromSellable = body.ExcludeFromSellable;

            await _db.SaveChangesAsync();
            await Log("UPDATED", "Location", loc.LocationCode, loc.LocationName, 1);
            return Ok(new { message = $"{loc.LocationName} updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/locations/{id:int}");
        }
    }

    [HttpDelete("locations/{id:int}")]
    public async Task<IActionResult> DeleteLocation(int id)
    {
        try
        {
            var loc = await _db.Locations.FirstOrDefaultAsync(l => l.LocationId == id);
            if (loc is null) return NotFound(new { message = "Location not found." });

            /* Deleting cascades in this schema, so a location holding stock would
               silently take its balances with it. Refuse instead. */
            var units = await _db.StockBalances.Where(s => s.LocationId == id).SumAsync(s => (int?)s.Quantity) ?? 0;
            if (units != 0)
                return BadRequest(new { message = $"{units} units still sit here. Move the stock to another location first." });

            if (loc.IsDefault)
                return BadRequest(new { message = "This is the default location. Make another one default first." });

            loc.IsActive = false;
            await _db.SaveChangesAsync();
            await Log("DELETED", "Location", loc.LocationCode, "Deactivated", 4);
            return Ok(new { message = $"{loc.LocationName} deactivated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete /api/admin/locations/{id:int}");
        }
    }

    [HttpGet("location-kinds")]
    public async Task<IActionResult> GetLocationKinds()
    {
        try
        {
            return Ok(await _db.LocationKinds.OrderBy(k => k.KindId)
                    .Select(k => new { id = k.KindId, key = k.KindKey, name = k.KindName }).ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/location-kinds");
        }
    }

    // ════════════════════ validation helpers ════════════════════

    private async Task ClearOtherDefaults(int? keepId)
    {
        var others = await _db.Locations.Where(l => l.IsDefault && l.LocationId != keepId).ToListAsync();
        foreach (var o in others) o.IsDefault = false;
    }

    private async Task<string?> ValidateLocation(LocationRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.Code)) return "Code is required.";
        if (string.IsNullOrWhiteSpace(b.Name)) return "Name is required.";
        if (!await _db.LocationKinds.AnyAsync(k => k.KindId == b.KindId)) return "Pick a valid location type.";
        if (!await _db.Cities.AnyAsync(c => c.CityId == b.CityId)) return "Pick a valid city.";

        var code = b.Code.Trim().ToUpperInvariant();
        if (await _db.Locations.AnyAsync(l => l.LocationCode.ToUpper() == code && l.LocationId != existingId))
            return "Another location already uses that code.";

        var name = b.Name.Trim().ToLower();
        if (await _db.Locations.AnyAsync(l => l.LocationName.ToLower() == name && l.LocationId != existingId))
            return "Another location already uses that name.";

        return null;
    }

    // ══════════════════════ request bodies ══════════════════════

    public record LocationRequest(
        string Code, string Name, int KindId, int CityId, string? Address,
        int? InChargeUserId, bool IsActive, bool IsDefault, bool ExcludeFromSellable);
}