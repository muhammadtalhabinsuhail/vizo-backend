using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Expense
{
    public int ExpenseId { get; set; }

    public string ExpenseNo { get; set; } = null!;

    public DateOnly ExpenseDate { get; set; }

    public int LocationId { get; set; }

    public string CategoryName { get; set; } = null!;

    public int ExpenseAccountId { get; set; }

    public int PaidFromAccountId { get; set; }

    public decimal Amount { get; set; }

    public string VendorName { get; set; } = null!;

    public int MethodId { get; set; }

    public string? Description { get; set; }

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual Account ExpenseAccount { get; set; } = null!;

    public virtual Location Location { get; set; } = null!;

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual Account PaidFromAccount { get; set; } = null!;

    public virtual PostingStatus Status { get; set; } = null!;
}
