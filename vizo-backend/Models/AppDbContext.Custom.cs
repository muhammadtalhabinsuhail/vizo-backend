using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// AppDbContext.cs is scaffolded straight from the Neon database, so anything
/// added by hand inside it is wiped the next time somebody runs
/// dotnet ef dbcontext scaffold. Everything this project adds therefore lives
/// here instead, attached through the OnModelCreatingPartial hook that the
/// scaffolder leaves behind for exactly this purpose.
///
/// Result: not one scaffolded file is edited, so re-scaffolding is safe.
///
/// NOTE FOR A FUTURE RE-SCAFFOLD: the "PasswordResetCode" table now exists on
/// Neon, so a fresh scaffold WILL generate its own PasswordResetCode.cs and its
/// own DbSet. If you re-scaffold, delete this file and Models/PasswordResetCode.cs
/// first, or you will get duplicate-definition errors.
/// </summary>
public partial class AppDbContext
{
    /// <summary>
    /// The one table this project added to the database.
    /// Created on Neon by backend/database/06_neon_auth.sql.
    /// </summary>
    public virtual DbSet<PasswordResetCode> PasswordResetCodes { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PasswordResetCode>(entity =>
        {
            entity.HasKey(e => e.ResetId).HasName("PasswordResetCode_pkey");

            entity.ToTable("PasswordResetCode");

            entity.HasIndex(e => e.UserId, "ix_prc_user");

            entity.Property(e => e.CodeHash).HasMaxLength(200);
            entity.Property(e => e.Attempts).HasDefaultValue((short)0);

            /* .WithMany() with no navigation argument: the relationship is
               declared to EF without either class needing a navigation
               property, which is what keeps the scaffolded User.cs untouched. */
            entity.HasOne<User>().WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_prc_user");
        });
    }
}
