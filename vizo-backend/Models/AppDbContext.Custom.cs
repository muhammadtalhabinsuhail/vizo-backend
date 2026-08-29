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

        /* ── columns added by backend/database/08_sales_documents.sql ──

           These live on scaffolded entities, so their lengths and defaults are
           declared here rather than in AppDbContext.cs -- same reason as
           everything else in this file: a re-scaffold must not lose them. */

        modelBuilder.Entity<SalesInvoice>(entity =>
        {
            entity.Property(e => e.PdfUrl).HasMaxLength(500);
            entity.Property(e => e.PdfPublicId).HasMaxLength(255);
            entity.Property(e => e.IsWalkIn).HasDefaultValue(false);
            entity.Property(e => e.WalkInName).HasMaxLength(150);
            entity.Property(e => e.WalkInPhone).HasMaxLength(30);
        });

        modelBuilder.Entity<SalesReturn>(entity =>
        {
            entity.Property(e => e.DecisionReason).HasMaxLength(300);

            /* Npgsql maps a bare DateTime to "timestamp WITH time zone" and
               then refuses to write one whose Kind is Unspecified -- which is
               exactly what ApiControllerBase.Now() produces, on purpose, for
               every other timestamp in this schema. Declaring the column type
               is what the scaffolder does for LoggedAt, VisitedAt and the
               rest; this column needs the same. */
            entity.Property(e => e.DecidedAt).HasColumnType("timestamp without time zone");

            entity.HasOne<User>().WithMany()
                .HasForeignKey(d => d.DecidedByUserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_SalesReturn_DecidedBy");
        });
    }
}
