using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers.Admin;

/// <summary>
/// Staff and party accounts: the list, one person, their activity trail, and
/// every change that can be made to them.
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
public class AdminUsersController : AdminControllerBase
{
    private readonly PushNotificationService _push;

    public AdminUsersController(AppDbContext db, IConfiguration cfg, ILogger<AdminUsersController> logger,
        IWebHostEnvironment env, PushNotificationService push)
        : base(db, cfg, logger, env) => _push = push;


    // ══════════════════════════════════════════════════════════════════
    //  USERS
    // ══════════════════════════════════════════════════════════════════

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers([FromQuery] string? q, [FromQuery] int page = 1,
                                              [FromQuery] int pageSize = 15, [FromQuery] bool? isActive = null)
    {
        try
        {
            var query = _db.Users.Where(u => u.Role.IsStaffRole);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(u =>
                    u.FullName.ToLower().Contains(term) ||
                    (u.Email != null && u.Email.ToLower().Contains(term)) ||
                    (u.Employee != null && u.Employee.EmployeeCode.ToLower().Contains(term)));
            }
            if (isActive.HasValue) query = query.Where(u => u.IsActive == isActive.Value);

            var total = await query.CountAsync();

            var rows = await query
                .OrderBy(u => u.UserId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(u => new
                {
                    id = u.UserId,
                    fullName = u.FullName,
                    email = u.Email,
                    phone = u.Phone,
                    employeeCode = u.Employee != null ? u.Employee.EmployeeCode : null,
                    roleId = u.RoleId,
                    roles = new[] { u.Role.RoleName },
                    locations = u.LocationsNavigation.Select(l => l.LocationCode).ToList(),
                    isActive = u.IsActive,
                    isLocked = u.Employee != null && u.Employee.IsLocked,
                    lastLoginAt = u.Employee != null ? u.Employee.LastLoginAt : null,
                    createdAt = u.CreatedAt
                })
                .ToListAsync();

            return Ok(new
            {
                items = rows.Select(r => new
                {
                    r.id, r.fullName, initials = Initials(r.fullName), r.email, r.phone,
                    r.employeeCode, r.roleId, r.roles, r.locations, r.isActive, r.isLocked,
                    r.lastLoginAt, r.createdAt
                }),
                total, page, pageSize
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/users");
        }
    }

    [HttpGet("users/stats")]
    public async Task<IActionResult> UserStats()
    {
        try
        {
            var staff = _db.Users.Where(u => u.Role.IsStaffRole);
            return Ok(new
            {
                total = await staff.CountAsync(),
                active = await staff.CountAsync(u => u.IsActive),
                locked = await staff.CountAsync(u => u.Employee != null && u.Employee.IsLocked)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/users/stats");
        }
    }

    [HttpGet("users/{id:int}")]
    public async Task<IActionResult> GetUser(int id)
    {
        try
        {
            var u = await _db.Users
                .Where(x => x.UserId == id)
                .Select(x => new
                {
                    id = x.UserId,
                    fullName = x.FullName,
                    email = x.Email,
                    phone = x.Phone,
                    employeeCode = x.Employee != null ? x.Employee.EmployeeCode : null,
                    roleId = x.RoleId,
                    roles = new[] { x.Role.RoleName },
                    roleKey = x.Role.RoleKey,
                    permissionCount = x.Role.Permissions.Count,
                    locations = x.LocationsNavigation.Select(l => new { l.LocationId, l.LocationCode, l.LocationName }).ToList(),
                    primaryLocationId = x.PrimaryLocationId,
                    isActive = x.IsActive,
                    isLocked = x.Employee != null && x.Employee.IsLocked,
                    lastLoginAt = x.Employee != null ? x.Employee.LastLoginAt : null,
                    createdAt = x.CreatedAt,
                    /* Still on a temporary password -- the same test
                       GET /api/account/status applies. */
                    mustChangePassword = x.MustChangePassword && x.TemporaryPasswordHash != null
                                         && x.TemporaryPasswordHash == x.PasswordHash,
                    temporaryPasswordIssuedAt = x.TemporaryPasswordIssuedAt
                })
                .FirstOrDefaultAsync();

            if (u is null) return NotFound(new { message = "User not found." });
            return Ok(new
            {
                u.id, u.fullName, initials = Initials(u.fullName), u.email, u.phone,
                u.employeeCode, u.roleId, u.roles, u.roleKey, u.permissionCount,
                u.locations, u.primaryLocationId, u.isActive, u.isLocked, u.lastLoginAt, u.createdAt,
                u.mustChangePassword, u.temporaryPasswordIssuedAt
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/users/{id:int}");
        }
    }

    [HttpGet("users/{id:int}/activity")]
    public async Task<IActionResult> UserActivity(int id, [FromQuery] int take = 20)
    {
        try
        {
            var rows = await _db.ActivityLogs
                .Where(a => a.UserId == id)
                .OrderByDescending(a => a.LoggedAt)
                .Take(take)
                .Select(a => new
                {
                    id = a.LogId,
                    action = a.ActionName,
                    entity = a.EntityType + " " + a.EntityReference,
                    detail = a.Detail,
                    ip = a.IpAddress,
                    time = a.LoggedAt,
                    severity = a.Severity.SeverityKey
                })
                .ToListAsync();
            return Ok(rows);
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/admin/users/{id:int}/activity");
        }
    }

    /// <summary>
    /// Creates a staff account with a password NOBODY CHOSE.
    ///
    /// Until 27 Sep the form sent "Vizo@1234" for every new person, so every
    /// account created from Setup shared one password that anybody who had ever
    /// been given an account knew -- and the "Send invite email" switch beside
    /// it did nothing at all (SendInvite was never read).
    ///
    /// Now the server generates a random temporary password, the account is
    /// flagged MustChangePassword, and the password is returned ONCE in this
    /// response for the Super Admin to hand over. With SendInvite on it is also
    /// emailed to the new person through Services/Mailer. Whatever the email
    /// does, the admin still sees the password, so a mail failure never leaves
    /// somebody without a way in. body.Password is ignored on purpose.
    /// </summary>
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] UserRequest body)
    {
        try
        {
            var problem = await ValidateUser(body, null);
            if (problem is not null) return BadRequest(new { message = problem });

            var role = await _db.Roles.FirstAsync(r => r.RoleId == body.RoleId);

            var user = new User
            {
                RoleId = role.RoleId,
                RequiresEmail = role.RequiresEmail,
                FullName = body.FullName.Trim(),
                Email = body.Email?.Trim().ToLowerInvariant(),
                Phone = body.Phone?.Trim(),
                IsActive = body.IsActive,
                CreatedAt = Today(),
            };

            /* ck_user_password wants a hash on every staff row, so the
               temporary one is written with the row itself. */
            var temporary = Credentials.NewTemporaryPassword();
            Credentials.SetTemporaryPassword(user, temporary, Now());

            if (body.LocationIds is { Count: > 0 })
                user.PrimaryLocationId = body.LocationIds[0];

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            _db.Employees.Add(new Employee
            {
                UserId = user.UserId,
                EmployeeCode = body.EmployeeCode!.Trim().ToUpperInvariant(),
                IsLocked = false,
                JoinedOn = Today()
            });

            if (body.LocationIds is { Count: > 0 })
            {
                var locs = await _db.Locations.Where(l => body.LocationIds.Contains(l.LocationId)).ToListAsync();
                foreach (var l in locs) user.LocationsNavigation.Add(l);
            }

            await _db.SaveChangesAsync();
            await Log("CREATED", "User", user.Email ?? user.FullName,
                      $"{role.RoleName} account created with a temporary password", 1);

            var (emailed, emailError) = body.SendInvite
                ? await TryMail(() => Mailer.SendTemporaryPasswordAsync(_cfg, user.Email!, user.FullName, temporary, isNewAccount: true),
                                "invite", user.Email!)
                : (false, null);

            /* -- F1 -- other admins only. Somebody gaining access to the system
               is an admin's business and nobody else's. */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.UserChanged,
                $"User added by {CurrentUserName()}",
                $"{user.FullName} -- {role.RoleName}.",
                url: $"/admin/users/{user.UserId}",
                exceptUserId: CurrentUserId());

            return Ok(new
            {
                id = user.UserId,
                message = $"{user.FullName} added.",
                email = user.Email,
                temporaryPassword = temporary,
                inviteRequested = body.SendInvite,
                emailed,
                emailError
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/users");
        }
    }

    [HttpPut("users/{id:int}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UserRequest body)
    {
        try
        {
            var user = await _db.Users.Include(u => u.Employee).Include(u => u.LocationsNavigation)
                .FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "User not found." });

            var problem = await ValidateUser(body, id);
            if (problem is not null) return BadRequest(new { message = problem });

            var role = await _db.Roles.FirstAsync(r => r.RoleId == body.RoleId);

            user.FullName = body.FullName.Trim();
            user.Email = body.Email?.Trim().ToLowerInvariant();
            user.Phone = body.Phone?.Trim();
            user.RoleId = role.RoleId;
            user.RequiresEmail = role.RequiresEmail;
            user.IsActive = body.IsActive;

            if (user.Employee is not null && !string.IsNullOrWhiteSpace(body.EmployeeCode))
                user.Employee.EmployeeCode = body.EmployeeCode.Trim().ToUpperInvariant();

            if (body.LocationIds is not null)
            {
                user.LocationsNavigation.Clear();
                var locs = await _db.Locations.Where(l => body.LocationIds.Contains(l.LocationId)).ToListAsync();
                foreach (var l in locs) user.LocationsNavigation.Add(l);
                user.PrimaryLocationId = body.LocationIds.Count > 0 ? body.LocationIds[0] : null;
            }

            await _db.SaveChangesAsync();
            await Log("UPDATED", "User", user.Email ?? user.FullName, "Account updated", 1);
            return Ok(new { message = $"{user.FullName} updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/users/{id:int}");
        }
    }

    [HttpPatch("users/{id:int}/active")]
    public async Task<IActionResult> SetUserActive(int id, [FromBody] BoolRequest body)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "User not found." });

            if (id == CurrentUserId() && !body.Value)
                return BadRequest(new { message = "You cannot deactivate the account you are signed in with." });

            user.IsActive = body.Value;
            await _db.SaveChangesAsync();
            await Log("UPDATED", "User", user.Email ?? user.FullName,
                      body.Value ? "Account activated" : "Account deactivated", 3);

            /* -- F1 -- deactivating somebody is the half of this that matters:
               it is how access is taken away, and it should be visible. */
            await _push.NotifyRoleAsync(
                "super-admin",
                NotificationKinds.UserChanged,
                $"User {(body.Value ? "activated" : "deactivated")} by {CurrentUserName()}",
                $"{user.FullName}'s account was {(body.Value ? "activated" : "deactivated")}.",
                url: $"/admin/users/{user.UserId}",
                exceptUserId: CurrentUserId());

            return Ok(new { message = body.Value ? "Account activated." : "Account deactivated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "update /api/admin/users/{id:int}/active");
        }
    }

    [HttpPatch("users/{id:int}/lock")]
    public async Task<IActionResult> SetUserLock(int id, [FromBody] BoolRequest body)
    {
        try
        {
            var emp = await _db.Employees.Include(e => e.User).FirstOrDefaultAsync(e => e.UserId == id);
            if (emp is null) return NotFound(new { message = "That user has no staff record." });

            if (id == CurrentUserId() && body.Value)
                return BadRequest(new { message = "You cannot lock the account you are signed in with." });

            emp.IsLocked = body.Value;
            await _db.SaveChangesAsync();
            await Log("UPDATED", "User", emp.User.Email ?? emp.User.FullName,
                      body.Value ? "Account locked" : "Account unlocked", 3);
            return Ok(new { message = body.Value ? "Account locked." : "Account unlocked." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "update /api/admin/users/{id:int}/lock");
        }
    }

    /// <summary>
    /// Emails the person a six-digit reset code. Their CURRENT PASSWORD KEEPS
    /// WORKING until they choose a new one.
    ///
    /// This used to overwrite the password hash with a random GUID and send
    /// nothing: the message said "must now reset via the code sent to ..." but
    /// no code was ever sent (the forgot-password action was commented out),
    /// so pressing it simply locked the person out for good. Now it issues the
    /// same kind of code /forgot-password issues -- same table, same expiry --
    /// and the person finishes on the Forgot password screen, where
    /// AuthController's verify-code and reset-password spend it.
    ///
    /// Unlike the anonymous endpoint, the Super Admin IS told when the email
    /// could not be sent: they are trusted, and they need to know to use a
    /// temporary password instead.
    /// </summary>
    [HttpPost("users/{id:int}/password-reset")]
    public async Task<IActionResult> SendResetCode(int id)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "User not found." });
            if (string.IsNullOrWhiteSpace(user.Email))
                return BadRequest(new { message = "That user has no email address to send a code to." });
            if (!user.IsActive)
                return BadRequest(new { message = "That account is deactivated. Activate it first -- a code is no use to somebody who cannot sign in." });

            var email = user.Email;
            var minutes = Credentials.CodeExpiryMinutes(_cfg);
            var code = await Credentials.IssueResetCodeAsync(_db, user.UserId, minutes, Now());

            var (emailed, emailError) = await TryMail(
                () => Mailer.SendResetCodeAsync(_cfg, email, user.FullName, code, minutes, askedByAdmin: true),
                "reset code", email);

            if (!emailed)
            {
                /* A code nobody received is a code nobody can use; spend it so
                   the table does not hold a live one that went nowhere. */
                var live = await _db.PasswordResetCodes
                    .Where(c => c.UserId == user.UserId && c.ConsumedAt == null).ToListAsync();
                foreach (var c in live) c.ConsumedAt = Now();
                await _db.SaveChangesAsync();

                return StatusCode(502, new
                {
                    message = "The reset code could not be emailed. Nothing on the account has changed -- " +
                              "set a temporary password instead and hand it over yourself.",
                    error = emailError
                });
            }

            await Log("PASSWORD_RESET_SENT", "User", email,
                      "Reset code emailed by the administrator; the current password still works", 3);

            return Ok(new
            {
                message = $"A reset code is on its way to {email}. It is valid for {minutes} minutes; " +
                          "their current password keeps working until they use it.",
                expiresInMinutes = minutes
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/users/{id:int}/password-reset");
        }
    }

    /// <summary>
    /// Replaces the password with a random temporary one, shown to the Super
    /// Admin ONCE in this response (and emailed too when asked), and makes the
    /// person choose their own at next sign-in.
    ///
    /// For the person with no working email, or who is standing next to the
    /// admin. Unlike the reset code this DOES stop the old password working --
    /// which is also what you want when a password may have been seen by
    /// somebody else.
    /// </summary>
    [HttpPost("users/{id:int}/temporary-password")]
    public async Task<IActionResult> SetTemporaryPassword(int id, [FromBody] TemporaryPasswordRequest? body)
    {
        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "User not found." });
            if (id == CurrentUserId())
                return BadRequest(new { message = "Change your own password from My Profile -> Security." });

            var temporary = Credentials.NewTemporaryPassword();
            Credentials.SetTemporaryPassword(user, temporary, Now());

            /* An outstanding reset code would be a second way round the
               temporary password; it dies with the old password. */
            var live = await _db.PasswordResetCodes
                .Where(c => c.UserId == user.UserId && c.ConsumedAt == null).ToListAsync();
            foreach (var c in live) c.ConsumedAt = Now();

            await _db.SaveChangesAsync();
            await Log("PASSWORD_TEMPORARY", "User", user.Email ?? user.FullName,
                      "Temporary password set by the administrator; must be changed at next sign-in", 3);

            var sendEmail = body?.SendEmail == true && !string.IsNullOrWhiteSpace(user.Email);
            var (emailed, emailError) = sendEmail
                ? await TryMail(() => Mailer.SendTemporaryPasswordAsync(_cfg, user.Email!, user.FullName, temporary, isNewAccount: false),
                                "temporary password", user.Email!)
                : (false, null);

            return Ok(new
            {
                message = $"{user.FullName} must choose a new password at next sign-in.",
                email = user.Email,
                temporaryPassword = temporary,
                emailRequested = sendEmail,
                emailed,
                emailError
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/admin/users/{id:int}/temporary-password");
        }
    }

    /// <summary>
    /// Runs one Mailer call and reports (sent?, short reason). The whole
    /// exception goes to the log; the Super Admin gets the mail server's own
    /// one-line reason, which is what they need to put the settings right.
    /// </summary>
    private async Task<(bool Sent, string? Error)> TryMail(Func<Task> send, string what, string to)
    {
        try
        {
            await send();
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not email the {What} to {To}", what, to);
            return (false, ex.GetBaseException().Message);
        }
    }

    /// <summary>Deactivate rather than delete: the audit trail, the orders
    /// they took and the entries they posted all still point here.</summary>
    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id, [FromBody] ReasonRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 5)
                return BadRequest(new { message = "A reason of at least 5 characters is required." });

            var user = await _db.Users.Include(u => u.Employee).FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return NotFound(new { message = "User not found." });
            if (id == CurrentUserId())
                return BadRequest(new { message = "You cannot delete the account you are signed in with." });

            user.IsActive = false;
            /* Not null: the schema's ck_user_password forbids a staff row without
               a hash. Overwrite it with a random one nobody holds instead -- the
               effect is the same and the constraint stays satisfied. */
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), 11);
            if (user.Employee is not null) user.Employee.IsLocked = true;
            await _db.SaveChangesAsync();

            await Log("DELETED", "User", user.Email ?? user.FullName, body.Reason.Trim(), 4);
            return Ok(new { message = $"{user.FullName} deactivated and access revoked." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete /api/admin/users/{id:int}");
        }
    }

    // ════════════════════ validation helpers ════════════════════

    private async Task<string?> ValidateUser(UserRequest b, int? existingId)
    {
        if (string.IsNullOrWhiteSpace(b.FullName) || b.FullName.Trim().Length < 2)
            return "Full name is required.";
        if (string.IsNullOrWhiteSpace(b.Email))
            return "Email is required for a staff account.";
        if (!b.Email.Contains('@') || !b.Email.Contains('.'))
            return "That email address does not look right.";
        if (string.IsNullOrWhiteSpace(b.EmployeeCode))
            return "Employee code is required.";
        if (!await _db.Roles.AnyAsync(r => r.RoleId == b.RoleId))
            return "Pick a valid role.";

        var email = b.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.Email != null && u.Email.ToLower() == email && u.UserId != existingId))
            return "Another account already uses that email address.";

        var code = b.EmployeeCode.Trim().ToUpperInvariant();
        if (await _db.Employees.AnyAsync(e => e.EmployeeCode.ToUpper() == code && e.UserId != existingId))
            return "Another account already uses that employee code.";

        return await ValidatePlace(b);
    }

    /* ══════════════════════════════════════════════════════════════════
       WHICH WAREHOUSE. WHICH ORDER DESK.
       ══════════════════════════════════════════════════════════════════

       "if warehouse account of muhammadzain will be created then it will be
        asked which warehouse -- either Lahore-Warehouse or Karachi-Warehouse"

       ONE role is tied to a PLACE rather than to the company as a whole:

         order-dept        packs and dispatches out of a particular order desk.

       The warehouse-keeper role this was written for no longer exists (this
       session) -- warehouse LOCATIONS remain, for transfers and for stock to
       sit at, but nobody signs in as "the warehouse" any more. The rule below
       is unchanged for order-dept, which had the same problem: /admin/users
       would create a clerk with no location, or three, or with the Claim Stock
       shelf -- and the queue then showed them every order in the company,
       which is exactly the report that came back.

       EXACTLY ONE, AND OF THE RIGHT KIND. Not "at least one": a keeper who
       belongs to two warehouses is a keeper whose queue is ambiguous, and the
       first thing anybody would ask on seeing it is which of the two the stock
       is actually on. If somebody genuinely covers both cities, make them two
       accounts or give them a back-office role -- that is a decision about how
       the business is run, and it should not be arrived at by ticking a second
       box on a form.

       Every other role keeps the old behaviour: as many locations as the owner
       wants to grant, because an accountant reading ledgers is not standing
       anywhere in particular.

       The KINDS come from "LocationKind" -- 1 warehouse, 3 department -- and
       the owner creates as many of each as there are cities at
       /admin/locations. Nothing here knows any location's id.
       ══════════════════════════════════════════════════════════════════ */

    /// <summary>The location kind each place-bound role must be attached to.</summary>
    private static readonly IReadOnlyDictionary<string, (string KindKey, string Noun)> PlaceBoundRoles =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["order-dept"] = ("department", "order department"),
        };

    private async Task<string?> ValidatePlace(UserRequest b)
    {
        var roleKey = await _db.Roles.Where(r => r.RoleId == b.RoleId)
            .Select(r => r.RoleKey).FirstOrDefaultAsync();

        if (roleKey is null || !PlaceBoundRoles.TryGetValue(roleKey, out var rule)) return null;

        var picked = b.LocationIds ?? new List<int>();

        if (picked.Count == 0)
            return $"Choose which {rule.Noun} this account belongs to.";

        if (picked.Count > 1)
            return $"A {rule.Noun} account belongs to exactly one {rule.Noun}. " +
                   $"Pick the one they work at -- {picked.Count} were selected.";

        var place = await _db.Locations.AsNoTracking()
            .Where(l => l.LocationId == picked[0])
            .Select(l => new { l.LocationName, l.IsActive, kind = l.Kind.KindKey, city = l.City.CityName })
            .FirstOrDefaultAsync();

        if (place is null) return $"Pick a valid {rule.Noun}.";

        if (!place.IsActive)
            return $"{place.LocationName} is not in use any more. Pick an active {rule.Noun}.";

        if (!string.Equals(place.kind, rule.KindKey, StringComparison.OrdinalIgnoreCase))
            return $"{place.LocationName} is not a {rule.Noun}. " +
                   $"Pick one of the {rule.Noun}s set up under Administration -> Locations.";

        return null;
    }

    // ══════════════════════ request bodies ══════════════════════

    public record UserRequest(
        string FullName, string? Email, string? Phone, string? EmployeeCode,
        int RoleId, List<int>? LocationIds, bool IsActive, bool SendInvite, string? Password);
    public record BoolRequest(bool Value);
    public record TemporaryPasswordRequest(bool SendEmail);

    // ══════════════════════ request bodies ════════════════════════════

    public record ReasonRequest(string? Reason);
}