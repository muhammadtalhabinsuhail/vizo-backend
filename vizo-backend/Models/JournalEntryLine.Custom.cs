namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// JournalEntryLine.cs is scaffolded and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here.
/// Created by backend/database/31_staff_ledgers.sql. Mapped by convention (a
/// plain nullable int with the column's own name) -- deliberately no
/// navigation property, so no relationship has to be configured in
/// AppDbContext.Custom.cs.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition once the column exists on the database being scaffolded.
/// </summary>
public partial class JournalEntryLine
{
    /// <summary>
    /// Which member of staff a line on 2140 Staff Payables belongs to -- the
    /// staff counterpart of <see cref="PartyUserId"/>. Null on every other line.
    /// </summary>
    public int? StaffId { get; set; }
}
