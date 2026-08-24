using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MimeKit;
using vizo_backend.Models;

/* "Claim" is a warranty claim in this domain, so the security one needs a
   name of its own wherever both are in scope. */
using SecurityClaim = System.Security.Claims.Claim;

namespace vizo_backend.Controllers;

/// <summary>
/// Sign-in, identity, and the emailed password-reset code.
/// Everything is controller-only: no services, no DTO classes, no interfaces.
/// Request bodies bind to inline records declared at the foot of this file.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthController> _logger;
    private readonly IWebHostEnvironment _env;

    public AuthController(AppDbContext db, IConfiguration cfg,
        ILogger<AuthController> logger, IWebHostEnvironment env)
    {
        _db = db;
        _cfg = cfg;
        _logger = logger;
        _env = env;
    }

    /* PostgreSQL columns here are "timestamp without time zone". Npgsql refuses
       a DateTime whose Kind is Utc for those, so every timestamp we write goes
       through this. */
    private static DateTime Now() =>
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    /* ═════════════════════════════ LOGIN ═════════════════════════════ */

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
                return BadRequest(new { message = "Email and password are required." });

            var email = body.Email.Trim().ToLowerInvariant();

            var user = await _db.Users
                .Include(u => u.Role)
                .Include(u => u.Employee)
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email);

            /* One message for "no such account" and "wrong password" alike -- a
               different reply for each tells an attacker which addresses exist. */
            if (user is null || string.IsNullOrEmpty(user.PasswordHash) ||
                !BCrypt.Net.BCrypt.Verify(body.Password, user.PasswordHash))
                return Unauthorized(new { message = "Email or password is incorrect." });

            if (!user.IsActive)
                return StatusCode(403, new { message = "This account has been deactivated. Contact your administrator." });

            if (user.Employee is not null && user.Employee.IsLocked)
                return StatusCode(423, new { message = "This account is locked.", locked = true });

            /* Only staff sign in. Customers and suppliers are records, not logins. */
            if (!user.Role.IsStaffRole)
                return StatusCode(403, new { message = "This account cannot sign in to the portal." });

            if (user.Employee is not null)
            {
                user.Employee.LastLoginAt = Now();
                await _db.SaveChangesAsync();
            }

            await WriteLog(user.UserId, "LOGIN", "UserSession", user.Email!, "Signed in", 5);

            var permissions = await PermissionsFor(user.RoleId);
            var (token, expiresAt) = IssueToken(user, permissions);

            return Ok(new
            {
                token,
                expiresAt,
                user = ShapeUser(user, permissions)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/auth/login");
        }
    }

    /* ═════════════════════════════ ME ════════════════════════════════ */

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        try
        {
            var id = CurrentUserId();
            if (id is null) return Unauthorized();

            var user = await _db.Users
                .Include(u => u.Role)
                .Include(u => u.Employee)
                .FirstOrDefaultAsync(u => u.UserId == id);

            if (user is null || !user.IsActive) return Unauthorized();

            var permissions = await PermissionsFor(user.RoleId);
            return Ok(ShapeUser(user, permissions));
        }
        catch (Exception ex)
        {
            return Fail(ex, "load C:/Program Files/Git/api/auth/me");
        }
    }

    /* ══════════════════════ FORGOT PASSWORD ══════════════════════════ */

    /// <summary>Step 1. Emails a six-digit code. Always answers 200.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest body)
    {
        var expiryMinutes = _cfg.GetValue("PasswordReset:CodeExpiryMinutes", 30);

        /* Deliberately identical whatever happens next: a reply that differs
           for a real address turns this endpoint into an account enumerator. */
        var generic = Ok(new
        {
            message = "If that address belongs to an account, a reset code is on its way.",
            expiresInMinutes = expiryMinutes
        });

        if (string.IsNullOrWhiteSpace(body.Email)) return generic;
        var email = body.Email.Trim().ToLowerInvariant();

        var user = await _db.Users
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email);

        if (user is null || !user.IsActive || !user.Role.IsStaffRole) return generic;

        /* Any code already outstanding for this person is dead the moment a
           new one is asked for. */
        var live = await _db.PasswordResetCodes
            .Where(c => c.UserId == user.UserId && c.ConsumedAt == null)
            .ToListAsync();
        foreach (var c in live) c.ConsumedAt = Now();

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        _db.PasswordResetCodes.Add(new PasswordResetCode
        {
            UserId = user.UserId,
            CodeHash = BCrypt.Net.BCrypt.HashPassword(code, 11),
            ExpiresAt = Now().AddMinutes(expiryMinutes),
            CreatedAt = Now(),
            Attempts = 0
        });
        await _db.SaveChangesAsync();

        try
        {
            await SendResetEmail(user.Email!, user.FullName, code, expiryMinutes);
        }
        catch (Exception ex)
        {
            /* The code is already stored. Report the delivery failure to the
               log rather than to the caller, who must not learn anything. */
            Console.WriteLine($"[forgot-password] SMTP failure for {user.Email}: {ex.Message}");
        }

        return generic;
    }

    /// <summary>Step 2. Checks the code without spending it, so the UI can
    /// move to the new-password screen before committing.</summary>
    [HttpPost("verify-code")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest body)
    {
        try
        {
            var check = await FindUsableCode(body.Email, body.Code);
            if (check.Error is not null) return BadRequest(new { message = check.Error });

            return Ok(new { message = "Code accepted.", valid = true });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/auth/verify-code");
        }
    }

    /// <summary>Step 3. Spends the code and sets the new password.</summary>
    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest body)
    {
        try
        {
            var problem = ValidatePassword(body.NewPassword);
            if (problem is not null) return BadRequest(new { message = problem });

            var check = await FindUsableCode(body.Email, body.Code);
            if (check.Error is not null) return BadRequest(new { message = check.Error });

            var entry = check.Entry!;
            var user = await _db.Users.FirstAsync(u => u.UserId == entry.UserId);

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.NewPassword, 11);
            entry.ConsumedAt = Now();

            /* Everything else outstanding dies with it. */
            var others = await _db.PasswordResetCodes
                .Where(c => c.UserId == user.UserId && c.ConsumedAt == null)
                .ToListAsync();
            foreach (var o in others) o.ConsumedAt = Now();

            await _db.SaveChangesAsync();
            await WriteLog(user.UserId, "PASSWORD_RESET", "User", user.Email ?? user.FullName,
                           "Password reset with an emailed code", 3);

            return Ok(new { message = "Password updated. You can sign in with it now." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/auth/reset-password");
        }
    }

    /// <summary>Signed-in change, current password required.</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body)
    {
        try
        {
            var id = CurrentUserId();
            if (id is null) return Unauthorized();

            var problem = ValidatePassword(body.NewPassword);
            if (problem is not null) return BadRequest(new { message = problem });

            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return Unauthorized();

            if (string.IsNullOrEmpty(user.PasswordHash) ||
                !BCrypt.Net.BCrypt.Verify(body.CurrentPassword, user.PasswordHash))
                return BadRequest(new { message = "Your current password is not right." });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.NewPassword, 11);
            await _db.SaveChangesAsync();
            await WriteLog(user.UserId, "PASSWORD_CHANGE", "User", user.Email ?? user.FullName,
                           "Password changed from the profile screen", 1);

            return Ok(new { message = "Password updated." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/auth/change-password");
        }
    }

    /// <summary>Bookkeeping only. The token is a bearer credential: the client
    /// drops it, and it expires on its own.</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var id = CurrentUserId();
            if (id is not null)
                await WriteLog(id.Value, "LOGOUT", "UserSession", User.FindFirstValue(ClaimTypes.Email) ?? "", "Signed out", 5);
            return Ok(new { message = "Signed out." });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/auth/logout");
        }
    }

    /* ═════════════════════════ helpers ═══════════════════════════════ */

    private int? CurrentUserId()
    {
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out var id) ? id : null;
    }

    private (string token, DateTime expiresAt) IssueToken(User user, List<string> permissions)
    {
        var jwt = _cfg.GetSection("Jwt");
        var minutes = jwt.GetValue("ExpiryMinutes", 480);
        var expires = DateTime.UtcNow.AddMinutes(minutes);

        var claims = new List<SecurityClaim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email ?? ""),
            new(ClaimTypes.Role, user.Role.RoleKey),
            new("roleId", user.RoleId.ToString()),
            new("locationId", user.PrimaryLocationId?.ToString() ?? ""),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        /* Permissions ride in the token so the API can gate on a capability
           rather than on a role name. */
        claims.AddRange(permissions.Select(p => new SecurityClaim("perm", p)));

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    private async Task<List<string>> PermissionsFor(int roleId) =>
        await _db.Roles
            .Where(r => r.RoleId == roleId)
            .SelectMany(r => r.Permissions.Select(p => p.PermissionKey))
            .ToListAsync();

    private static object ShapeUser(User user, List<string> permissions) => new
    {
        userId = user.UserId,
        fullName = user.FullName,
        email = user.Email,
        phone = user.Phone,
        roleId = user.RoleId,
        role = user.Role.RoleKey,
        roleLabel = user.Role.RoleName,
        homePath = user.Role.HomePath,
        initials = Initials(user.FullName),
        primaryLocationId = user.PrimaryLocationId,
        employeeCode = user.Employee?.EmployeeCode,
        isActive = user.IsActive,
        permissions
    };

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private static string? ValidatePassword(string? pw)
    {
        if (string.IsNullOrWhiteSpace(pw)) return "Password is required.";
        if (pw.Length < 8) return "Password must be at least 8 characters.";
        if (!pw.Any(char.IsUpper)) return "Password needs an uppercase letter.";
        if (!pw.Any(char.IsLower)) return "Password needs a lowercase letter.";
        if (!pw.Any(char.IsDigit)) return "Password needs a number.";
        return null;
    }

    /// <summary>
    /// Finds the outstanding code for an address and checks the six digits
    /// against its hash. Every failure burns an attempt, so a six-digit code
    /// cannot be walked through.
    /// </summary>
    private async Task<(PasswordResetCode? Entry, string? Error)> FindUsableCode(string? email, string? code)
    {
        const string generic = "That code is not valid, or it has expired. Ask for a new one.";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(code))
            return (null, generic);

        var normalised = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == normalised);
        if (user is null) return (null, generic);

        var entry = await _db.PasswordResetCodes
            .Where(c => c.UserId == user.UserId && c.ConsumedAt == null)
            .OrderByDescending(c => c.ResetId)
            .FirstOrDefaultAsync();

        if (entry is null) return (null, generic);

        if (entry.ExpiresAt < Now())
        {
            entry.ConsumedAt = Now();
            await _db.SaveChangesAsync();
            return (null, generic);
        }

        var max = _cfg.GetValue("PasswordReset:MaxAttempts", 5);
        if (entry.Attempts >= max)
        {
            entry.ConsumedAt = Now();
            await _db.SaveChangesAsync();
            return (null, "Too many wrong attempts. Ask for a new code.");
        }

        if (!BCrypt.Net.BCrypt.Verify(code.Trim(), entry.CodeHash))
        {
            entry.Attempts++;
            await _db.SaveChangesAsync();
            return (null, generic);
        }

        return (entry, null);
    }

    private async Task SendResetEmail(string to, string name, string code, int minutes)
    {
        var s = _cfg.GetSection("EmailSettings");

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s["SenderName"], s["SenderEmail"]));
        msg.To.Add(new MailboxAddress(name, to));
        msg.Subject = $"{code} is your AdvPOS password reset code";

        msg.Body = new BodyBuilder
        {
            HtmlBody = $@"
<div style=""font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:0 auto;padding:32px;color:#0f172a"">
  <h2 style=""margin:0 0 8px;font-size:20px"">Reset your AdvPOS password</h2>
  <p style=""margin:0 0 24px;color:#475569"">Hello {System.Net.WebUtility.HtmlEncode(name)}, use this code to continue.</p>
  <div style=""background:#0f172a;color:#facc15;font-size:34px;font-weight:700;letter-spacing:10px;
              text-align:center;padding:20px;border-radius:10px"">{code}</div>
  <p style=""margin:24px 0 0;color:#475569"">It is valid for {minutes} minutes and can be used once.</p>
  <p style=""margin:12px 0 0;color:#94a3b8;font-size:13px"">
    Did not ask for this? Nothing has changed on your account. You can ignore this message.
  </p>
</div>",
            TextBody = $"Your AdvPOS password reset code is {code}. It is valid for {minutes} minutes."
        }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(s["SmtpHost"], s.GetValue("SmtpPort", 587), SecureSocketOptions.StartTls);
        await client.AuthenticateAsync(s["SenderEmail"], s["SenderPassword"]);
        await client.SendAsync(msg);
        await client.DisconnectAsync(true);
    }

    private async Task WriteLog(int? userId, string action, string entityType, string reference, string detail, int severityId)
    {
        _db.ActivityLogs.Add(new ActivityLog
        {
            UserId = userId,
            ActionName = action,
            EntityType = entityType,
            EntityReference = reference,
            Detail = detail,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            SeverityId = severityId,
            LoggedAt = Now()
        });
        await _db.SaveChangesAsync();
    }

    /* ═══════════════════ request bodies (inline records) ═════════════ */

    public record LoginRequest(string Email, string Password);
    public record ForgotPasswordRequest(string Email);
    public record VerifyCodeRequest(string Email, string Code);
    public record ResetPasswordRequest(string Email, string Code, string NewPassword);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    /// <summary>
    /// The single failure path for this controller.
    ///
    /// Logs the whole exception server-side, then answers with JSON the screen
    /// can show: what was being attempted, and the real message off the BASE
    /// exception -- Npgsql puts the useful text there (a constraint name, a
    /// null violation) while the outer DbUpdateException only ever says
    /// "An error occurred while saving the entity changes".
    ///
    /// The stack trace is attached in Development only.
    /// </summary>
    private IActionResult Fail(Exception ex, string what)
    {
        _logger.LogError(ex, "Failed to {What} ({Method} {Path})",
            what, Request.Method, Request.Path);

        return StatusCode(500, new
        {
            message = $"Could not {what}.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = _env.IsDevelopment() ? ex.ToString() : null
        });
    }

}
