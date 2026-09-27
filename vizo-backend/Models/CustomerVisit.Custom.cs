using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// CustomerVisit.cs is scaffolded and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the columns this project added live here.
/// Created by backend/database/39_visits_and_delivery_confirmation.sql and
/// mapped with annotations (the timestamp needs its column type spelled out --
/// HANDOFF trap 12).
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition once the columns exist on the database being scaffolded.
/// </summary>
public partial class CustomerVisit
{
    /// <summary>When the rep means to go back. A "Followup" outcome without a date is a note nobody is reminded of.</summary>
    public DateOnly? NextFollowUpDate { get; set; }

    /// <summary>
    /// When the row was typed, as opposed to <see cref="VisitedAt"/> (when the
    /// visit happened). Logging yesterday's visits this morning is normal; a
    /// month back-dated the night before a review is worth seeing.
    /// </summary>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? LoggedAt { get; set; }

    /// <summary>The phone's own accuracy radius, in metres, for Latitude/Longitude.</summary>
    public int? GpsAccuracyM { get; set; }
}
