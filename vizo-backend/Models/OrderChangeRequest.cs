namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN -- not produced by scaffolding.
///
/// A salesperson cannot edit or delete an order. When they need one changed
/// they file this: which order, which kind of change, and why. The Super Admin
/// approves it with a tick or refuses it with a cross, from their dashboard.
///
/// An APPROVED request is a ONE-SHOT KEY. It permits that person to make that
/// one change to that one order, and it is spent the moment they do -- the row
/// moves to USED. Without that it would quietly become a standing permission
/// that nobody remembers granting.
///
/// Created on Neon by backend/database/15_order_workflow.sql.
/// </summary>
public partial class OrderChangeRequest
{
    public int RequestId { get; set; }

    public int OrderId { get; set; }

    public int RequestedByUserId { get; set; }

    /// <summary>"EDIT" or "DELETE".</summary>
    public string Kind { get; set; } = null!;

    public string Reason { get; set; } = null!;

    /// <summary>"PENDING" | "APPROVED" | "DECLINED" | "USED".</summary>
    public string Status { get; set; } = "PENDING";

    public int? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual User RequestedByUser { get; set; } = null!;

    public virtual User? DecidedByUser { get; set; }
}
