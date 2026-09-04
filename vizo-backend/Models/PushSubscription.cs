namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN -- not produced by scaffolding.
///
/// One row per BROWSER, not per user. The same person signing in on a laptop,
/// a phone and a shop terminal produces three rows, and a push has to go to all
/// three or they will swear the notification never arrived.
///
/// Created on Neon by backend/database/13_push_subscriptions.sql.
///
/// AFTER A RE-SCAFFOLD: the table now exists on Neon, so a fresh scaffold will
/// generate its own PushSubscription.cs and DbSet. Delete this file and the
/// mapping in AppDbContext.Custom.cs first, or you will get duplicate
/// definitions -- the same trap PasswordResetCode and DocumentFile carry.
/// </summary>
public partial class PushSubscription
{
    public int PushSubscriptionId { get; set; }

    public int UserId { get; set; }

    /// <summary>
    /// The push service URL the browser handed us. Unique: re-subscribing the
    /// same browser must update the row, not add a second one, or every push
    /// arrives twice.
    /// </summary>
    public string Endpoint { get; set; } = null!;

    /// <summary>The subscription's public key, for payload encryption.</summary>
    public string P256dh { get; set; } = null!;

    /// <summary>The subscription's auth secret, for payload encryption.</summary>
    public string Auth { get; set; } = null!;

    /// <summary>
    /// What the person was using when they subscribed. Only so a settings
    /// screen can say "Chrome on Windows" rather than showing a 200-character
    /// endpoint URL.
    /// </summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Bumped on every successful push. A subscription that has not been
    /// written to in months is almost certainly a browser nobody opens.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    public virtual User User { get; set; } = null!;
}
