using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class BackupHistory
{
    public int BackupId { get; set; }

    public DateTime StartedAt { get; set; }

    public int BackupTypeId { get; set; }

    public int StatusId { get; set; }

    public decimal SizeMb { get; set; }

    public string Destination { get; set; } = null!;

    public int DurationSeconds { get; set; }

    public string? ChecksumHash { get; set; }

    public int? TriggeredByUserId { get; set; }

    public virtual BackupType BackupType { get; set; } = null!;

    public virtual BackupStatus Status { get; set; } = null!;

    public virtual User? TriggeredByUser { get; set; }
}
