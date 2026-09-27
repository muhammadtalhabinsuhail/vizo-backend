using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- the stored backup files.
///
/// Its own file, like AppDbContext.Expenses.cs, so parallel sessions add their
/// tables without editing the same lines (AppDbContext.cs itself is Talha's).
/// The mapping is all data annotations on <see cref="BackupFile"/>; the FK to
/// "BackupHistory" is enforced by the database (38_backup_files.sql) and is
/// deliberately not an EF navigation, which would mean editing the scaffolded
/// BackupHistory.cs.
/// </summary>
public partial class AppDbContext
{
    public virtual DbSet<BackupFile> BackupFiles { get; set; } = null!;
}
