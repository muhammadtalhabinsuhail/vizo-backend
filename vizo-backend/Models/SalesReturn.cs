using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SalesReturn
{
    public int ReturnId { get; set; }

    public string ReturnNo { get; set; } = null!;

    public int InvoiceId { get; set; }

    public int CustomerUserId { get; set; }

    public int LocationId { get; set; }

    public DateOnly ReturnDate { get; set; }

    public string Reason { get; set; } = null!;

    public int RefundMethodId { get; set; }

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    /* Added by backend/database/08_sales_documents.sql. */

    /// <summary>Why the return was approved, posted or rejected. Required on reject.</summary>
    public string? DecisionReason { get; set; }

    public int? DecidedByUserId { get; set; }

    public DateTime? DecidedAt { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual SalesInvoice Invoice { get; set; } = null!;

    public virtual Location Location { get; set; } = null!;

    public virtual PaymentMethod RefundMethod { get; set; } = null!;

    public virtual ICollection<SalesReturnItem> SalesReturnItems { get; set; } = new List<SalesReturnItem>();

    public virtual ReturnStatus Status { get; set; } = null!;
}
