using System;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN MODEL -- not produced by scaffolding.
///
/// Backs the "PasswordResetCode" table, which is the ONLY table this project
/// added to the database that was not in the original design. Created on Neon
/// by backend/database/06_neon_auth.sql.
///
/// Column names are PascalCase to match the Neon database, so no HasColumnName
/// mapping is needed -- see AppDbContext.Custom.cs.
///
/// A BCrypt hash of the six digits is stored, never the digits themselves: a
/// leaked table must not hand somebody a working code. Attempts is what stops
/// a six-digit code being brute forced -- the endpoint refuses the code once
/// it reaches PasswordReset:MaxAttempts and the row is dead.
/// </summary>
public partial class PasswordResetCode
{
    public int ResetId { get; set; }

    public int UserId { get; set; }

    public string CodeHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public short Attempts { get; set; }

    public DateTime CreatedAt { get; set; }

    /* Deliberately NO 'public virtual User User' navigation.
       Adding one would mean editing the scaffolded User.cs to add the other
       half of the pair, and that edit is wiped by the next scaffold. The FK is
       configured in AppDbContext.Custom.cs with .WithMany() -- no navigation
       on either side -- so every scaffolded file stays untouched. Queries use
       the UserId column directly, which is all this table is ever asked for. */
}
