using System;
using System.ComponentModel.DataAnnotations.Schema;

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

    /* THE THREE TIMESTAMPS ARE DECLARED "timestamp without time zone" (27 Sep).
       Without the declaration EF maps a DateTime to timestamptz, and Npgsql
       then refuses the Kind=Unspecified value Now() produces (HANDOFF trap
       12): "Cannot write DateTime with Kind=Unspecified to PostgreSQL type
       'timestamp with time zone'". So the first code ever issued failed to
       save, and reset-password could never mark one spent -- the reset flow
       had never worked end to end. The columns themselves were always plain
       TIMESTAMP (06_neon_auth.sql); only the mapping was wrong. */

    [Column(TypeName = "timestamp without time zone")]
    public DateTime ExpiresAt { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime? ConsumedAt { get; set; }

    public short Attempts { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime CreatedAt { get; set; }

    /* Deliberately NO 'public virtual User User' navigation.
       Adding one would mean editing the scaffolded User.cs to add the other
       half of the pair, and that edit is wiped by the next scaffold. The FK is
       configured in AppDbContext.Custom.cs with .WithMany() -- no navigation
       on either side -- so every scaffolded file stays untouched. Queries use
       the UserId column directly, which is all this table is ever asked for. */
}
