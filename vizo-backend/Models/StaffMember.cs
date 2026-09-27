using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// One person on the payroll, with or without a login.
///
/// HAND-WRITTEN, not scaffolded. Created by backend/database/31_staff_ledgers.sql,
/// which explains at length why this is not a "User" row: a driver or a helper
/// must never be able to sign in, and keeping them out of the table the sign-in
/// code reads is the only way to be certain of that.
///
/// A member of staff who DOES sign in is linked through <see cref="UserId"/>.
/// Their money lives on 2140 Staff Payables, one line per movement, with
/// "JournalEntryLine"."StaffId" saying whose line it is -- see
/// Models/JournalEntryLine.Custom.cs and Services/LedgerPosting.cs.
/// </summary>
[Table("StaffMember")]
public class StaffMember
{
    [Key]
    public int StaffId { get; set; }

    [MaxLength(20)]
    public string StaffCode { get; set; } = null!;

    [MaxLength(120)]
    public string FullName { get; set; } = null!;

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(20)]
    public string? Cnic { get; set; }

    public int StaffCategoryId { get; set; }

    [ForeignKey(nameof(StaffCategoryId))]
    public StaffCategory Category { get; set; } = null!;

    /// <summary>The login this person also has, if any. Null for a driver or a helper.</summary>
    public int? UserId { get; set; }

    [Precision(14, 2)]
    public decimal MonthlySalary { get; set; }

    /// <summary>
    /// Owed to them (positive) or by them (negative) on the day the ledger
    /// started -- in the natural, CREDIT sense of a liability, the same way
    /// "Account"."OpeningBalance" is stored.
    /// </summary>
    [Precision(14, 2)]
    public decimal OpeningBalance { get; set; }

    public DateOnly JoinedOn { get; set; }

    public bool IsActive { get; set; } = true;

    [MaxLength(300)]
    public string? Notes { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime CreatedAt { get; set; }
}
