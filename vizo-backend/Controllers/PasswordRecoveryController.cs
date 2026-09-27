using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// Step 1 of "Forgot password": POST /api/auth/forgot-password.
///
/// ─────────────────────────── WHY THIS FILE EXISTS ──────────────────────────
///
/// /forgot-password has always posted here, and the action in AuthController
/// was commented out -- so the screen answered 404, the person saw "Could not
/// send the code", and nobody could ever reset a forgotten password. Steps 2
/// and 3 (verify-code, reset-password) are still live in AuthController and
/// spend PasswordResetCode rows; only the step that CREATES one was missing.
///
/// AuthController is Talha's file and is not edited, so step 1 lives here, on
/// the same "api/auth" prefix (routing is case-insensitive, so "api/Auth" and
/// "api/auth" are one path). IF THE COMMENTED ACTION IN AuthController IS EVER
/// RESTORED, delete this file -- two actions on one route is an
/// AmbiguousMatchException at the first request.
///
/// The logic follows the commented original line for line where it can: the
/// same generic reply, staff roles only, every outstanding code spent, six
/// digits hashed with BCrypt cost 11, expiry from PasswordReset:CodeExpiryMinutes.
/// Three things are added, all about not telling a stranger anything:
///
///   1. THE EMAIL IS SENT AFTER THE REPLY, not before. SMTP takes a second or
///      three; an unknown address answers at once. Awaiting the send made the
///      response TIME say which addresses are real even though the words did not.
///   2. An unknown address pays for a BCrypt hash too, for the same reason.
///   3. One code per address per minute. Without it, anyone can make a staff
///      member's phone buzz as fast as they can click, and each click kills the
///      code the real person is about to type.
///
/// SMTP failures go to the server log and never to the caller.
/// </summary>
[Route("api/auth")]
[ApiController]
[AllowAnonymous]
public class PasswordRecoveryController : ApiControllerBase
{
    public PasswordRecoveryController(AppDbContext db, IConfiguration cfg,
        ILogger<PasswordRecoveryController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /* In memory on purpose, like AuthController's failed-attempt counter: a
       one-minute throttle is not a record, and it resets with the process,
       which is harmless. Only REAL addresses are ever stored, so a stranger
       cannot grow it by inventing addresses. */
    private static readonly ConcurrentDictionary<int, DateTime> LastIssued = new();
    private static readonly TimeSpan Throttle = TimeSpan.FromSeconds(60);

    /* A real BCrypt hash to verify against when the address is unknown, so both
       paths do the same amount of work. Generated once per process. */
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword("not-a-real-code", 11));

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest body)
    {
        var expiryMinutes = Credentials.CodeExpiryMinutes(_cfg);

        /* Deliberately identical whatever happens next: a reply that differs for
           a real address turns this endpoint into an account enumerator. */
        var generic = Ok(new
        {
            message = "If that address belongs to an account, a reset code is on its way.",
            expiresInMinutes = expiryMinutes
        });

        try
        {
            if (string.IsNullOrWhiteSpace(body?.Email)) return generic;
            var email = body.Email.Trim().ToLowerInvariant();

            var user = await _db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == email);

            /* Staff only -- customers and suppliers are records, not logins --
               and only accounts that could sign in afterwards. */
            if (user is null || !user.IsActive || !user.Role.IsStaffRole)
            {
                BCrypt.Net.BCrypt.Verify("000000", DummyHash.Value);
                return generic;
            }

            var now = DateTime.UtcNow;
            if (LastIssued.TryGetValue(user.UserId, out var last) && now - last < Throttle)
            {
                /* Same work as the other two paths, or a quick answer here would
                   say "this address is real, and was asked for a moment ago". */
                BCrypt.Net.BCrypt.Verify("000000", DummyHash.Value);
                return generic;
            }
            LastIssued[user.UserId] = now;

            var code = await Credentials.IssueResetCodeAsync(_db, user.UserId, expiryMinutes, Now());
            await WriteLog(user.UserId, "PASSWORD_RESET_REQUESTED", user.Email!,
                           "A reset code was requested from the sign-in screen", 3);

            /* Fire and forget -- see point 1 above. Mailer needs only the
               configuration, never the DbContext, so nothing request-scoped is
               used after the response has gone. */
            var cfg = _cfg;
            var logger = _logger;
            var to = user.Email!;
            var name = user.FullName;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Mailer.SendResetCodeAsync(cfg, to, name, code, expiryMinutes);
                }
                catch (Exception ex)
                {
                    /* The code is already stored. Report the delivery failure
                       to the log rather than to the caller, who must not learn
                       anything. */
                    logger.LogError(ex, "[forgot-password] Could not email the reset code to {Email}", to);
                }
            });

            return generic;
        }
        catch (Exception ex)
        {
            /* Even a database failure answers with the generic reply: a 500 only
               for real addresses would be the same leak by another route. */
            _logger.LogError(ex, "[forgot-password] failed");
            return generic;
        }
    }

    /* ApiControllerBase.Log writes CurrentUserId(), which is 0 for an anonymous
       caller and would break the ActivityLog foreign key. Here the actor is
       the account the request is about. */
    private async Task WriteLog(int userId, string action, string reference, string detail, int severityId)
    {
        try
        {
            _db.ActivityLogs.Add(new ActivityLog
            {
                UserId = userId,
                ActionName = action,
                EntityType = "User",
                EntityReference = reference,
                Detail = detail,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                SeverityId = severityId,
                LoggedAt = Now()
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write the {Action} log row", action);
        }
    }

    public record ForgotPasswordRequest(string? Email);
}
