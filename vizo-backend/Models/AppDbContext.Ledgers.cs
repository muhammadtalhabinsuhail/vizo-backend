using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- the tables the customer and staff ledgers added
/// (backend/database/31_staff_ledgers.sql, 32_ledger_entry_items.sql).
///
/// In a file of its own so three parallel branches can each add their tables
/// without touching AppDbContext.cs (Talha's) or AppDbContext.Custom.cs. Every
/// entity here is mapped with data annotations on its own class, so nothing
/// needs adding to OnModelCreatingPartial.
/// </summary>
public partial class AppDbContext
{
    public virtual DbSet<StaffCategory> StaffCategories { get; set; } = null!;

    public virtual DbSet<StaffMember> StaffMembers { get; set; } = null!;

    public virtual DbSet<LedgerEntryItem> LedgerEntryItems { get; set; } = null!;
}
