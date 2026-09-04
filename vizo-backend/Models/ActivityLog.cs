using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ActivityLog
{
    public int LogId { get; set; }

    public int? UserId { get; set; }

    public string ActionName { get; set; } = null!;

    public string EntityType { get; set; } = null!;

    public string EntityReference { get; set; } = null!;

    public string? Detail { get; set; }

    public string? IpAddress { get; set; }

    public int? LocationId { get; set; }

    public int SeverityId { get; set; }

    public DateTime LoggedAt { get; set; }

    public virtual Location? Location { get; set; }

    public virtual SeverityLevel Severity { get; set; } = null!;

    public virtual User? User { get; set; }
}
