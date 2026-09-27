using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN MODEL -- not produced by scaffolding.
///
/// The .zip a backup run produced: one CSV per table plus manifest.json and
/// RESTORE.txt. One row per <see cref="BackupHistory"/> row that still has its
/// file. Created by backend/database/38_backup_files.sql; mapped entirely by
/// annotation, and registered in AppDbContext.Backups.cs.
///
/// Its own table so that listing the history never pulls the bytes, and so a
/// backup can leave this table out of itself -- otherwise each backup would
/// carry every earlier one inside it and grow without end.
/// </summary>
[Table("BackupFile")]
public class BackupFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int BackupId { get; set; }

    [MaxLength(120)]
    public string FileName { get; set; } = null!;

    public byte[] Content { get; set; } = null!;

    public long SizeBytes { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime CreatedAt { get; set; }
}
