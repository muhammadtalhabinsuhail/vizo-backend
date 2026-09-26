using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// Expense.cs is scaffolded and is overwritten by the next dotnet ef dbcontext
/// scaffold, so the column added by backend/database/35_expense_sheets.sql
/// lives here, mapped by annotation.
///
/// AFTER A RE-SCAFFOLD: delete this file -- the column will then be generated
/// inside Expense.cs itself.
/// </summary>
public partial class Expense
{
    /// <summary>
    /// The day sheet this expense is a line of. Every expense has one after
    /// migration 35; it stays nullable only so the column could be added to a
    /// table that already had rows.
    /// </summary>
    public int? SheetId { get; set; }

    [ForeignKey(nameof(SheetId))]
    public virtual ExpenseSheet? Sheet { get; set; }
}
