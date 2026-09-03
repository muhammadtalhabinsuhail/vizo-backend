namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// SalesOrder.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here.
/// Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/15_order_workflow.sql.
///
/// AFTER A RE-SCAFFOLD: the column exists on Neon now, so a fresh scaffold will
/// generate ConfirmRemindedAt in SalesOrder.cs itself. Delete this file then,
/// or you will get a duplicate-definition error.
/// </summary>
public partial class SalesOrder
{
    /// <summary>
    /// When the Super Admin was last nudged that this order is still waiting on
    /// their decision.
    ///
    /// The reminder repeats every six hours until they confirm or decline. This
    /// column is how the job knows it has already asked, so restarting the API
    /// does not fire the whole backlog at them again.
    ///
    /// Null means never reminded.
    /// </summary>
    public DateTime? ConfirmRemindedAt { get; set; }
}
