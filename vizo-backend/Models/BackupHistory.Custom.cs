using System.ComponentModel.DataAnnotations;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// BackupHistory.cs is scaffolded and is overwritten by the next dotnet ef
/// dbcontext scaffold, so the columns added by
/// backend/database/38_backup_files.sql live here, mapped by annotation.
///
/// AFTER A RE-SCAFFOLD: delete this file.
/// </summary>
public partial class BackupHistory
{
    /// <summary>How many tables went into the file. Null only on rows from before migration 38.</summary>
    public int? TableCount { get; set; }

    /// <summary>Rows across every table in the file.</summary>
    public long? RowTotal { get; set; }

    /// <summary>Why a FAILED run failed, in the database's own words.</summary>
    [MaxLength(500)]
    public string? ErrorMessage { get; set; }
}
