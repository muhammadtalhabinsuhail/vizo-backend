namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN -- not produced by scaffolding.
///
/// One row per person per notification kind they have deliberately turned OFF
/// or partially off. A MISSING ROW MEANS ON.
///
/// Storing only the exceptions is the whole point: a new notification kind
/// starts switched on for everybody with no backfill, and the table only ever
/// holds what people have actually changed.
///
/// Created on Neon by backend/database/13_push_subscriptions.sql.
/// </summary>
public partial class NotificationPreference
{
    public int PreferenceId { get; set; }

    public int UserId { get; set; }

    /// <summary>Matches PushNotificationService.Kind, e.g. "ORDER_CREATED".</summary>
    public string Kind { get; set; } = null!;

    /// <summary>Send this kind to their devices.</summary>
    public bool PushEnabled { get; set; } = true;

    /// <summary>Write this kind into the bell, even if push is off.</summary>
    public bool BellEnabled { get; set; } = true;

    public virtual User User { get; set; } = null!;
}
