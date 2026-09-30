using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN (database/42_accountant_approves_and_seen_markers.sql).
///
/// What one person has already seen in one area of the sidebar: the highest
/// order id (Area "orders") or customer id (Area "customers") they had seen
/// when they last opened that page. The sidebar's "new" badge is everything
/// above it -- see Controllers/SidebarBadgesController.cs.
/// </summary>
[Table("UserSeenMarker")]
[PrimaryKey(nameof(UserId), nameof(Area))]
public class UserSeenMarker
{
    public int UserId { get; set; }

    [MaxLength(40)]
    public string Area { get; set; } = null!;

    public int LastSeenId { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime SeenAt { get; set; }
}
