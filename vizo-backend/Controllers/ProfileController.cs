using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// The signed-in person's own record: profile, preferences and sign-in history.
///
/// WHY THIS IS A SEPARATE FILE AND NOT PART OF AuthController.
/// AuthController is one of the five files Talha owns (see HANDOFF.md); editing
/// it invites a merge conflict on every pull. Everything here is scoped to
/// "me" -- it never takes a user id from the caller, only from the token -- so
/// it stands on its own without reaching into the auth flow. Changing your own
/// password still lives at POST /api/Auth/change-password, which already works;
/// it is deliberately NOT duplicated here.
///
/// Controller-only by design: no DTO classes, no services, no interfaces, no
/// repositories. Request bodies bind to the records at the foot of the file and
/// responses are anonymous objects shaped to match what the screen renders.
///
/// Every action is try/catch and reports through Fail(), so a failure reaches
/// the browser as JSON carrying the real exception message.
/// </summary>
[Route("api/profile")]
[ApiController]
[Authorize]
public class ProfileController : ApiControllerBase
{
    public ProfileController(AppDbContext db, IConfiguration cfg, ILogger<ProfileController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }


    // ══════════════════════════════════════════════════════════════════
    //  MY PROFILE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Everything /profile renders. The four "Account Activity" tiles used to be
    /// hard-coded ("248 logins", "3 devices"); they are counted off ActivityLog
    /// here instead, so a number on that screen is now a number from the
    /// database or it is not shown at all.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Me()
    {
        try
        {
            var id = CurrentUserId();
            if (id == 0) return Unauthorized();

            var user = await _db.Users
                .Include(u => u.Role)
                .Include(u => u.Employee)
                .Include(u => u.PrimaryLocation)
                .FirstOrDefaultAsync(u => u.UserId == id);

            if (user is null) return NotFound(new { message = "Your user record no longer exists." });

            /* One pass over this person's LOGIN rows rather than three queries.
               ActivityLog is where AuthController records every sign-in. */
            var logins = await _db.ActivityLogs
                .Where(l => l.UserId == id && l.ActionName == "LOGIN")
                .Select(l => new { l.LoggedAt, l.IpAddress })
                .ToListAsync();

            return Ok(new
            {
                userId = user.UserId,
                fullName = user.FullName,
                email = user.Email,
                phone = user.Phone,
                initials = Initials(user.FullName),
                roleId = user.RoleId,
                roleLabel = user.Role.RoleName,
                isActive = user.IsActive,
                createdAt = user.CreatedAt,
                primaryLocationId = user.PrimaryLocationId,
                primaryLocationName = user.PrimaryLocation == null ? null : user.PrimaryLocation.LocationName,
                employeeCode = user.Employee == null ? null : user.Employee.EmployeeCode,
                joinedOn = user.Employee == null ? (DateOnly?)null : user.Employee.JoinedOn,
                isLocked = user.Employee != null && user.Employee.IsLocked,
                lastLoginAt = user.Employee == null ? null : user.Employee.LastLoginAt,

                /* Real counts. distinct IPs is the closest honest answer to
                   "devices" -- there is no device table, so the screen says
                   "known IPs" rather than inventing one. */
                totalLogins = logins.Count,
                lastSeenAt = logins.Count == 0 ? (DateTime?)null : logins.Max(l => l.LoggedAt),
                knownIpCount = logins.Where(l => l.IpAddress != null).Select(l => l.IpAddress).Distinct().Count()
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/profile");
        }
    }

    /// <summary>
    /// Updates only the three fields a person may change about themselves.
    /// Email, role and employee code are deliberately not editable here -- email
    /// is the login identity and role is a Super Admin decision, both of which
    /// belong to /admin/users.
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest body)
    {
        try
        {
            var id = CurrentUserId();
            if (id == 0) return Unauthorized();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "Your user record no longer exists." });

            if (string.IsNullOrWhiteSpace(body.FullName))
                return BadRequest(new { message = "Your name cannot be blank." });

            if (body.PrimaryLocationId is int locId)
            {
                var exists = await _db.Locations.AnyAsync(l => l.LocationId == locId && l.IsActive);
                if (!exists) return BadRequest(new { message = "That location is not one you can pick." });
            }

            user.FullName = body.FullName.Trim();
            user.Phone = string.IsNullOrWhiteSpace(body.Phone) ? null : body.Phone.Trim();
            user.PrimaryLocationId = body.PrimaryLocationId;

            await _db.SaveChangesAsync();
            await Log("PROFILE_UPDATED", "User", user.UserId.ToString(),
                      $"{user.FullName} updated their own profile", 1);

            return Ok(new { message = "Profile saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/profile");
        }
    }

    /// <summary>Locations the "Default Location" picker may offer.</summary>
    [HttpGet("locations")]
    public async Task<IActionResult> Locations()
    {
        try
        {
            return Ok(await _db.Locations
                .Where(l => l.IsActive)
                .OrderByDescending(l => l.IsDefault)
                .ThenBy(l => l.LocationName)
                .Select(l => new { id = l.LocationId, code = l.LocationCode, name = l.LocationName })
                .ToListAsync());
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/profile/locations");
        }
    }


    // ══════════════════════════════════════════════════════════════════
    //  PREFERENCES
    // ══════════════════════════════════════════════════════════════════

    /* "UserPreference" is a key/value table -- (UserId, PrefKey) -> PrefValue --
       so a new preference costs a row, not a migration. These are the keys the
       /profile/preferences screen writes; anything else stored against the user
       is returned in `all` and simply left alone. */
    private static readonly string[] KnownPrefKeys =
    {
        "theme", "notify.email", "notify.push", "notify.whatsapp", "notify.inApp",
        "list.density", "list.pageSize", "fmt.date", "fmt.number"
    };

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        try
        {
            var id = CurrentUserId();
            if (id == 0) return Unauthorized();

            var rows = await _db.UserPreferences
                .Where(p => p.UserId == id)
                .Select(p => new { p.PrefKey, p.PrefValue })
                .ToListAsync();

            var map = rows.ToDictionary(r => r.PrefKey, r => r.PrefValue);

            /* Answer with a complete object so the screen never has to guess a
               default -- an unset toggle reads the same way every time. */
            return Ok(new
            {
                theme = map.GetValueOrDefault("theme", "system"),
                notifyEmail = map.GetValueOrDefault("notify.email", "true") == "true",
                notifyPush = map.GetValueOrDefault("notify.push", "false") == "true",
                notifyWhatsapp = map.GetValueOrDefault("notify.whatsapp", "true") == "true",
                notifyInApp = map.GetValueOrDefault("notify.inApp", "true") == "true",
                listDensity = map.GetValueOrDefault("list.density", "comfortable"),
                listPageSize = int.TryParse(map.GetValueOrDefault("list.pageSize", "25"), out var n) ? n : 25,
                dateFormat = map.GetValueOrDefault("fmt.date", "dmy"),
                numberFormat = map.GetValueOrDefault("fmt.number", "intl"),
                all = map
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/profile/preferences");
        }
    }

    /// <summary>
    /// Upsert. Sends only the keys that changed; a null field is left as it was
    /// rather than being blanked, so the screen can save one toggle at a time.
    /// </summary>
    [HttpPut("preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] UpdatePreferencesRequest body)
    {
        try
        {
            var id = CurrentUserId();
            if (id == 0) return Unauthorized();

            var incoming = new Dictionary<string, string?>
            {
                ["theme"] = body.Theme,
                ["notify.email"] = body.NotifyEmail?.ToString().ToLowerInvariant(),
                ["notify.push"] = body.NotifyPush?.ToString().ToLowerInvariant(),
                ["notify.whatsapp"] = body.NotifyWhatsapp?.ToString().ToLowerInvariant(),
                ["notify.inApp"] = body.NotifyInApp?.ToString().ToLowerInvariant(),
                ["list.density"] = body.ListDensity,
                ["list.pageSize"] = body.ListPageSize?.ToString(),
                ["fmt.date"] = body.DateFormat,
                ["fmt.number"] = body.NumberFormat
            };

            var existing = await _db.UserPreferences.Where(p => p.UserId == id).ToListAsync();

            foreach (var (key, value) in incoming)
            {
                if (value is null) continue;                       // not sent -> not touched
                if (!KnownPrefKeys.Contains(key)) continue;        // never store a key we do not own

                var row = existing.FirstOrDefault(p => p.PrefKey == key);
                if (row is null)
                    _db.UserPreferences.Add(new UserPreference { UserId = id, PrefKey = key, PrefValue = value });
                else
                    row.PrefValue = value;
            }

            await _db.SaveChangesAsync();
            return Ok(new { message = "Preferences saved." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/profile/preferences");
        }
    }


    // ══════════════════════════════════════════════════════════════════
    //  SIGN-IN HISTORY
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// What /profile/security shows in place of the old fake "3 active devices".
    ///
    /// These are sign-in EVENTS, not live sessions, and the screen says so. The
    /// JWT this API issues is a bearer token with no server-side session record,
    /// so there is nothing to revoke and no honest way to list "active" devices
    /// or sign one out. Building that needs a session table -- written up in
    /// backend/database/db_code_changes.txt section 6 rather than faked here.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions([FromQuery] int take = 10)
    {
        try
        {
            var id = CurrentUserId();
            if (id == 0) return Unauthorized();

            if (take is < 1 or > 100) take = 10;

            var rows = await _db.ActivityLogs
                .Where(l => l.UserId == id &&
                           (l.ActionName == "LOGIN" || l.ActionName == "LOGOUT" ||
                            l.ActionName == "PASSWORD_CHANGE" || l.ActionName == "PASSWORD_RESET"))
                .OrderByDescending(l => l.LoggedAt)
                .Take(take)
                .Select(l => new
                {
                    id = l.LogId,
                    action = l.ActionName,
                    detail = l.Detail,
                    ip = l.IpAddress,
                    at = l.LoggedAt
                })
                .ToListAsync();

            return Ok(rows);
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/profile/sessions");
        }
    }


    // ══════════════════════════════════════════════════════════════════
    //  REQUEST BODIES
    // ══════════════════════════════════════════════════════════════════

    public record UpdateProfileRequest(string FullName, string? Phone, int? PrimaryLocationId);

    public record UpdatePreferencesRequest(
        string? Theme,
        bool? NotifyEmail,
        bool? NotifyPush,
        bool? NotifyWhatsapp,
        bool? NotifyInApp,
        string? ListDensity,
        int? ListPageSize,
        string? DateFormat,
        string? NumberFormat);
}
