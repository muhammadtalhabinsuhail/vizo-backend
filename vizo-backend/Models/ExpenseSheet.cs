using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN, not scaffolded. One day of expenses at one location.
///
/// The owner asked for "one invoice per day holding all of that day's
/// expenses" in place of one voucher per expense. The day is this row; the
/// expenses themselves stay "Expense" rows (every report, the nightly insights
/// and the AI summary already read them) and point back here through
/// <see cref="Expense.SheetId"/>.
///
/// Created by backend/database/35_expense_sheets.sql. Mapped entirely with
/// data annotations and registered in AppDbContext.Expenses.cs, so neither the
/// scaffolded AppDbContext.cs nor AppDbContext.Custom.cs had to change.
///
/// NOTE FOR A FUTURE RE-SCAFFOLD: the table exists on the database once 35 has
/// run there, so a fresh scaffold will generate its own ExpenseSheet.cs. Delete
/// this file and AppDbContext.Expenses.cs first -- same trap as DocumentFile.
/// </summary>
[Table("ExpenseSheet")]
public class ExpenseSheet
{
    [Key]
    public int SheetId { get; set; }

    /// <summary>EXS-26-0001, from the "expense.sheet" document series.</summary>
    [MaxLength(20)]
    public string SheetNo { get; set; } = null!;

    public DateOnly SheetDate { get; set; }

    public int LocationId { get; set; }

    /// <summary>
    /// PostingStatus: DRAFT while it is still being typed, POSTED once the
    /// accountant approves the day, REVERSED once that approval is undone.
    /// Every line carries the same status as its sheet, except the handful of
    /// pre-sheet expenses migration 35 filed that were already reversed.
    /// </summary>
    public int StatusId { get; set; }

    /// <summary>
    /// The ONE journal entry approval wrote: a debit per line and a credit per
    /// paid-from account. NULL while draft, and NULL on a sheet migration 35
    /// made from expenses that had each been posted on their own.
    /// </summary>
    public int? EntryId { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime CreatedAt { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime? UpdatedAt { get; set; }

    public int? ApprovedByUserId { get; set; }

    [Column(TypeName = "timestamp without time zone")]
    public DateTime? ApprovedAt { get; set; }

    public int? ReversedByUserId { get; set; }

    /// <summary>
    /// Set when the sheet is reversed. The one-sheet-per-day index only counts
    /// sheets where this is NULL, which is what lets the corrected day be
    /// opened again once the wrong one has been undone.
    /// </summary>
    [Column(TypeName = "timestamp without time zone")]
    public DateTime? ReversedAt { get; set; }

    [MaxLength(500)]
    public string? ReversalReason { get; set; }

    [ForeignKey(nameof(LocationId))]
    public virtual Location Location { get; set; } = null!;

    [ForeignKey(nameof(StatusId))]
    public virtual PostingStatus Status { get; set; } = null!;

    [ForeignKey(nameof(EntryId))]
    public virtual JournalEntry? Entry { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public virtual User CreatedByUser { get; set; } = null!;

    [ForeignKey(nameof(ApprovedByUserId))]
    public virtual User? ApprovedByUser { get; set; }

    [ForeignKey(nameof(ReversedByUserId))]
    public virtual User? ReversedByUser { get; set; }

    [InverseProperty(nameof(Expense.Sheet))]
    public virtual ICollection<Expense> Lines { get; set; } = new List<Expense>();
}
