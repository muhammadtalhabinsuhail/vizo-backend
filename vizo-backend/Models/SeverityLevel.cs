using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SeverityLevel
{
    public int SeverityId { get; set; }

    public string SeverityKey { get; set; } = null!;

    public string SeverityName { get; set; } = null!;

    public virtual ICollection<ActivityLog> ActivityLogs { get; set; } = new List<ActivityLog>();

    public virtual ICollection<Notification> Notifications { get; set; } = new List<Notification>();
}
