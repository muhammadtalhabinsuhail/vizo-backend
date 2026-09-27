using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// User.cs is scaffolded and is overwritten by the next dotnet ef dbcontext
/// scaffold, so the columns added by backend/database/37_must_change_password.sql
/// live here, mapped by annotation (AppDbContext.cs is Talha's and is not
/// touched).
///
/// AFTER A RE-SCAFFOLD: delete this file -- the columns will then be generated
/// inside User.cs itself.
/// </summary>
public partial class User
{
    /// <summary>
    /// The person signed in with a password somebody else chose -- the one the
    /// server generated when the Super Admin created the account, or a temporary
    /// one the Super Admin set later -- and must pick their own before using
    /// the app. The web app reads it from GET /api/account/status straight
    /// after sign-in and sends them to /setup.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// The hash of that temporary password, exactly as it was written to
    /// <see cref="PasswordHash"/>. It is what makes the flag honest whichever
    /// door the change comes through: /setup clears the flag itself, but the
    /// Security screen and the emailed reset code go through AuthController
    /// (Talha's, not edited), which knows nothing about the flag. So the rule is
    /// "must change while PasswordHash is STILL the temporary one" -- the moment
    /// it differs, the flag is spent. It is a copy of a hash already in the same
    /// row, so it reveals nothing that row did not.
    /// </summary>
    [Column(TypeName = "varchar(100)")]
    public string? TemporaryPasswordHash { get; set; }

    /// <summary>When the temporary password was issued (Pakistan time, like every timestamp here).</summary>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? TemporaryPasswordIssuedAt { get; set; }
}
