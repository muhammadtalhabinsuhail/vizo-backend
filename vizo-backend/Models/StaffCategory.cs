using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// A kind of staff -- Salesman, Driver, Office, Helper and whatever else the
/// accountant adds from the Staff Ledgers screen.
///
/// HAND-WRITTEN, not scaffolded. Created by backend/database/31_staff_ledgers.sql
/// and mapped with data annotations (the DbSet is in AppDbContext.Ledgers.cs),
/// so AppDbContext.cs and AppDbContext.Custom.cs are not touched.
///
/// A list of its own rather than "Role": a role decides what somebody may SIGN
/// IN to, and most of the people this list describes never sign in at all.
/// </summary>
[Table("StaffCategory")]
public class StaffCategory
{
    [Key]
    public int StaffCategoryId { get; set; }

    [MaxLength(60)]
    public string CategoryName { get; set; } = null!;
}
