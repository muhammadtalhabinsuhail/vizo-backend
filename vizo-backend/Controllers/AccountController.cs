using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;
using vizo_backend.Services;

namespace vizo_backend.Controllers;

/// <summary>
/// The signed-in person's must-change-password state, and the change that
/// clears it.
///
/// ─────────────────────────── WHY A SECOND CHANGE-PASSWORD ──────────────────
///
/// POST /api/Auth/change-password already exists and works (the Security
/// screen uses it). But it lives in AuthController, Talha's file, and it knows
/// nothing about <see cref="User.MustChangePassword"/>. /setup needs a change
/// that also clears that flag in the same save, so it gets its own endpoint
/// here that does exactly what his does -- same rules, same BCrypt cost, same
/// log row -- plus the flag.
///
/// The Security screen can keep using his: GET status notices on its own that
/// the password is no longer the temporary one (see
/// <see cref="Credentials.StillOnTemporaryPassword"/>) and clears the flag, so
/// whichever door the change came through, nobody is sent to /setup twice.
///
/// Everything is "me": no user id is ever taken from the caller.
/// </summary>
[Route("api/account")]
[ApiController]
[Authorize]
public class AccountController : ApiControllerBase
{
    public AccountController(AppDbContext db, IConfiguration cfg, ILogger<AccountController> logger,
        IWebHostEnvironment env) : base(db, cfg, logger, env) { }

    /// <summary>
    /// { mustChangePassword } -- read by the web app straight after sign-in
    /// and on every app load. The edge proxy cannot ask the database, so this
    /// is the check that sends someone on a temporary password to /setup.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        try
        {
            var id = CurrentUserId();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return Unauthorized();

            /* The flag is set but the password has since changed through a door
               that does not know about it (Security screen, emailed code): the
               requirement is met, so spend the flag now. */
            if (user.MustChangePassword && !Credentials.StillOnTemporaryPassword(user))
            {
                Credentials.ClearTemporaryPassword(user);
                await _db.SaveChangesAsync();
            }

            return Ok(new
            {
                mustChangePassword = user.MustChangePassword,
                temporaryPasswordIssuedAt = user.TemporaryPasswordIssuedAt
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load /api/account/status");
        }
    }

    /// <summary>
    /// Same as POST /api/Auth/change-password, and clears the must-change flag.
    /// The current password is required even when it is the temporary one: a
    /// token left signed in on a shared counter PC must not be enough to take
    /// the account over.
    /// </summary>
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest body)
    {
        try
        {
            var id = CurrentUserId();
            var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == id);
            if (user is null) return Unauthorized();

            var problem = Credentials.ValidatePassword(body.NewPassword);
            if (problem is not null) return BadRequest(new { message = problem, field = "newPassword" });

            if (string.IsNullOrEmpty(user.PasswordHash) || string.IsNullOrEmpty(body.CurrentPassword) ||
                !BCrypt.Net.BCrypt.Verify(body.CurrentPassword, user.PasswordHash))
            {
                return BadRequest(new
                {
                    message = user.MustChangePassword
                        ? "That is not the temporary password you were given."
                        : "Your current password is not right.",
                    field = "currentPassword"
                });
            }

            /* Choosing the temporary password again would leave the flag's whole
               point unmet -- somebody else has seen it. */
            if (BCrypt.Net.BCrypt.Verify(body.NewPassword, user.PasswordHash))
                return BadRequest(new { message = "Choose a password different from the one you signed in with.", field = "newPassword" });

            var wasTemporary = user.MustChangePassword;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(body.NewPassword, 11);
            Credentials.ClearTemporaryPassword(user);
            await _db.SaveChangesAsync();

            await Log("PASSWORD_CHANGE", "User", user.Email ?? user.FullName,
                      wasTemporary ? "Temporary password replaced at first sign-in" : "Password changed", 1);

            return Ok(new { message = "Password updated.", mustChangePassword = false });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save /api/account/change-password");
        }
    }

    public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
}
