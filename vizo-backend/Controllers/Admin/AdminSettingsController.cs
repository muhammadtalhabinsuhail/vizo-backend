using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Application settings, the company record, and the shared lookup bundle the
/// admin screens load their dropdowns from.
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
public class AdminSettingsController : AdminControllerBase
{
    public AdminSettingsController(AppDbContext db, IConfiguration cfg, ILogger<AdminSettingsController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  SETTINGS AND COMPANY
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        try
        {
            return Ok(await _db.AppSettings.OrderBy(s => s.SettingId)
                    .Select(s => new
                    {
                        id = s.SettingId,
                        group = s.SettingGroup,
                        key = s.SettingKey,
                        value = s.SettingValue,
                        description = s.Description
                    })
                    .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/settings");
        }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] List<SettingRequest> body)
    {
        try
        {
            if (body is null || body.Count == 0) return BadRequest(new { message = "Nothing to save." });

            var keys = body.Select(b => b.Key).ToList();
            var rows = await _db.AppSettings.Where(s => keys.Contains(s.SettingKey)).ToListAsync();

            foreach (var b in body)
            {
                var row = rows.FirstOrDefault(r => r.SettingKey == b.Key);
                if (row is null) continue;
                row.SettingValue = b.Value ?? "";
            }

            await _db.SaveChangesAsync();
            await Log("UPDATED", "AppSetting", "settings", $"{body.Count} settings saved", 3);
            return Ok(new { message = "Settings saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/settings");
        }
    }

    [HttpGet("company")]
    public async Task<IActionResult> GetCompany()
    {
        try
        {
            var c = await _db.Companies
                .Select(x => new
                {
                    id = x.CompanyId,
                    companyName = x.CompanyName,
                    legalName = x.LegalName,
                    addressLine = x.AddressLine,
                    cityId = x.CityId,
                    city = x.City.CityName,
                    country = x.Country,
                    phone = x.Phone,
                    email = x.Email,
                    ntn = x.Ntn,
                    strn = x.Strn,
                    fiscalYearStartMonth = x.FiscalYearStartMonth,
                    currencyCode = x.CurrencyCode,
                    currencySymbol = x.CurrencySymbol,
                    foreignRate = x.ForeignRate
                })
                .FirstOrDefaultAsync();

            if (c is null) return NotFound(new { message = "No company profile has been set up." });
            return Ok(c);
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/company");
        }
    }

    [HttpPut("company")]
    public async Task<IActionResult> UpdateCompany([FromBody] CompanyRequest body)
    {
        try
        {
            var c = await _db.Companies.FirstOrDefaultAsync();
            if (c is null) return NotFound(new { message = "No company profile has been set up." });

            if (string.IsNullOrWhiteSpace(body.CompanyName)) return BadRequest(new { message = "Company name is required." });
            if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@'))
                return BadRequest(new { message = "A valid company email is required." });
            if (body.FiscalYearStartMonth is < 1 or > 12)
                return BadRequest(new { message = "Fiscal year start month must be 1 to 12." });
            if (!await _db.Cities.AnyAsync(x => x.CityId == body.CityId))
                return BadRequest(new { message = "Pick a valid city." });

            c.CompanyName = body.CompanyName.Trim();
            c.LegalName = body.LegalName?.Trim() ?? c.LegalName;
            c.AddressLine = body.AddressLine?.Trim() ?? c.AddressLine;
            c.CityId = body.CityId;
            c.Country = body.Country?.Trim() ?? c.Country;
            c.Phone = body.Phone?.Trim() ?? c.Phone;
            c.Email = body.Email.Trim();
            c.Ntn = body.Ntn?.Trim() ?? c.Ntn;
            c.Strn = body.Strn?.Trim() ?? c.Strn;
            c.FiscalYearStartMonth = (short)body.FiscalYearStartMonth;
            c.CurrencyCode = body.CurrencyCode?.Trim() ?? c.CurrencyCode;
            c.CurrencySymbol = body.CurrencySymbol?.Trim() ?? c.CurrencySymbol;

            await _db.SaveChangesAsync();
            await Log("UPDATED", "Company", c.CompanyName, "Company profile updated", 3);
            return Ok(new { message = "Company profile saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/company");
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  LOOKUPS  (one call to fill every dropdown on the panel)
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("lookups")]
    public async Task<IActionResult> Lookups()
    {
        try
        {
            return Ok(new
            {
                roles = await _db.Roles.OrderBy(r => r.RoleId)
                    .Select(r => new { id = r.RoleId, key = r.RoleKey, name = r.RoleName, description = r.Description, permissionCount = r.Permissions.Count })
                    .ToListAsync(),
                locations = await _db.Locations.Where(l => l.IsActive).OrderBy(l => l.LocationId)
                    .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                    .ToListAsync(),
                locationKinds = await _db.LocationKinds.OrderBy(k => k.KindId)
                    .Select(k => new { id = k.KindId, key = k.KindKey, name = k.KindName })
                    .ToListAsync(),
                cities = await _db.Cities.OrderBy(c => c.CityName)
                    .Select(c => new { id = c.CityId, name = c.CityName, province = c.Province.ProvinceName })
                    .ToListAsync(),
                provinces = await _db.Provinces.OrderBy(p => p.ProvinceId)
                    .Select(p => new { id = p.ProvinceId, name = p.ProvinceName })
                    .ToListAsync(),
                staff = await _db.Users.Where(u => u.Role.IsStaffRole && u.IsActive).OrderBy(u => u.FullName)
                    .Select(u => new { id = u.UserId, name = u.FullName, role = u.Role.RoleName })
                    .ToListAsync(),
                accountGroups = await _db.AccountGroups.OrderBy(g => g.GroupId)
                    .Select(g => new { id = g.GroupId, name = g.GroupName })
                    .ToListAsync()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/lookups");
        }
    }

    // ══════════════════════ request bodies ══════════════════════

    public record SettingRequest(string Key, string? Value);

    public record CompanyRequest(
        string CompanyName, string? LegalName, string? AddressLine, int CityId, string? Country,
        string? Phone, string Email, string? Ntn, string? Strn, int FiscalYearStartMonth,
        string? CurrencyCode, string? CurrencySymbol);
}