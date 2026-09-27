using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- the daily expense sheets.
///
/// Its own file rather than a block in AppDbContext.Custom.cs, so the three
/// sessions working in parallel on 26 Sep each add their tables without
/// editing the same lines. The mapping is all data annotations on
/// <see cref="ExpenseSheet"/> and <see cref="Expense.SheetId"/>; nothing here
/// needs OnModelCreatingPartial.
///
/// Created by backend/database/35_expense_sheets.sql.
/// </summary>
public partial class AppDbContext
{
    public virtual DbSet<ExpenseSheet> ExpenseSheets { get; set; } = null!;
}
