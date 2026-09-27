using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// The small pieces of password handling that more than one controller needs:
/// issuing a reset code, generating and recording a temporary password, and
/// the password rules.
///
/// Static helpers, not a service -- the project brief is no DI services, and
/// every function here is handed the DbContext it works on. The three callers
/// are PasswordRecoveryController (anonymous forgot-password),
/// AdminUsersController (new user, reset code, temporary password) and
/// AccountController (/setup's change and the must-change status).
///
/// The rules and the code mechanics MIRROR AuthController (Talha's file, not
/// edited), because its verify-code and reset-password actions are the ones
/// that spend what is issued here: same table, same BCrypt cost, same expiry
/// setting, same "every outstanding code dies when a new one is issued".
/// </summary>
public static class Credentials
{
    /// <summary>
    /// Same four rules as AuthController.ValidatePassword, which reset-password
    /// and change-password enforce. A temporary password generated below always
    /// satisfies them, so the person can sign in with it and the rule is never
    /// the reason a first sign-in fails.
    /// </summary>
    public static string? ValidatePassword(string? pw)
    {
        if (string.IsNullOrWhiteSpace(pw)) return "Password is required.";
        if (pw.Length < 8) return "Password must be at least 8 characters.";
        if (!pw.Any(char.IsUpper)) return "Password needs an uppercase letter.";
        if (!pw.Any(char.IsLower)) return "Password needs a lowercase letter.";
        if (!pw.Any(char.IsDigit)) return "Password needs a number.";
        return null;
    }

    /* No 0/O, 1/l/I: the password is read off a screen or an email and typed
       by hand, often on a phone, and those pairs are where it goes wrong. */
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";

    /// <summary>
    /// Twelve characters from a cryptographic source, in three groups of four
    /// (<c>Kp7m-Xw3r-Tq9z</c>), with at least one upper, one lower and one digit
    /// guaranteed. About 68 bits -- far beyond guessing in the window before
    /// the person replaces it, and short enough to read out over the phone.
    /// </summary>
    public static string NewTemporaryPassword()
    {
        var all = Upper + Lower + Digits;
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++) chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        /* Force one of each class into three different random positions. */
        var slots = Enumerable.Range(0, 12).OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(3).ToArray();
        chars[slots[0]] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[slots[1]] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[slots[2]] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];

        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    /// <summary>
    /// Replaces the password with <paramref name="password"/> and marks it as
    /// one the person must change. Does not save.
    /// </summary>
    public static void SetTemporaryPassword(User user, string password, DateTime now)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password, 11);
        user.PasswordHash = hash;
        user.MustChangePassword = true;
        user.TemporaryPasswordHash = hash;
        user.TemporaryPasswordIssuedAt = now;
    }

    /// <summary>
    /// True while the person still signs in with the temporary password. A
    /// change through ANY route -- /setup, the Security screen, an emailed
    /// reset code -- writes a new hash, and this turns false on its own.
    /// </summary>
    public static bool StillOnTemporaryPassword(User user) =>
        user.MustChangePassword &&
        user.TemporaryPasswordHash is not null &&
        user.TemporaryPasswordHash == user.PasswordHash;

    /// <summary>Clears the must-change flag. Does not save.</summary>
    public static void ClearTemporaryPassword(User user)
    {
        user.MustChangePassword = false;
        user.TemporaryPasswordHash = null;
        user.TemporaryPasswordIssuedAt = null;
    }

    /// <summary>
    /// Issues a fresh six-digit reset code for this person and returns the
    /// digits (the table only ever holds their BCrypt hash). Any code already
    /// outstanding is spent first, exactly as the original forgot-password did.
    /// Saves.
    ///
    /// The code is spent by POST /api/Auth/verify-code and /reset-password,
    /// which read PasswordReset:MaxAttempts; the expiry here reads
    /// PasswordReset:CodeExpiryMinutes, the same key AuthController's
    /// commented-out original read.
    /// </summary>
    public static async Task<string> IssueResetCodeAsync(AppDbContext db, int userId, int expiryMinutes, DateTime now)
    {
        var live = await db.PasswordResetCodes
            .Where(c => c.UserId == userId && c.ConsumedAt == null)
            .ToListAsync();
        foreach (var c in live) c.ConsumedAt = now;

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        db.PasswordResetCodes.Add(new PasswordResetCode
        {
            UserId = userId,
            CodeHash = BCrypt.Net.BCrypt.HashPassword(code, 11),
            ExpiresAt = now.AddMinutes(expiryMinutes),
            CreatedAt = now,
            Attempts = 0
        });
        await db.SaveChangesAsync();
        return code;
    }

    /// <summary>The configured code lifetime, defaulting to 30 minutes as the original did.</summary>
    public static int CodeExpiryMinutes(IConfiguration cfg) => cfg.GetValue("PasswordReset:CodeExpiryMinutes", 30);
}
