using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Notification
{
    public int NotificationId { get; set; }

    public int UserId { get; set; }

    public int SeverityId { get; set; }

    public string Icon { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string Body { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public bool IsRead { get; set; }

    /// <summary>
    /// Where this notification points, as an in-app path (e.g. /sales/orders/42).
    ///
    /// Null when there is nothing to open -- a backup finishing has no page --
    /// and the bell simply does not make those rows clickable. Relative on
    /// purpose, so the same row works on localhost, staging and production.
    /// </summary>
    public string? Url { get; set; }

    public virtual SeverityLevel Severity { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
