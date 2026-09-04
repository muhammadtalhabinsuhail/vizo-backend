using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseReturn
{
    public int PrId { get; set; }

    public string ReturnNo { get; set; } = null!;

    public int PiId { get; set; }

    public int SupplierUserId { get; set; }

    public int LocationId { get; set; }

    public DateOnly ReturnDate { get; set; }

    public string Reason { get; set; } = null!;

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual Employee CreatedByUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual PurchaseInvoice Pi { get; set; } = null!;

    public virtual ICollection<PurchaseReturnItem> PurchaseReturnItems { get; set; } = new List<PurchaseReturnItem>();

    public virtual ReturnStatus Status { get; set; } = null!;

    public virtual Party SupplierUser { get; set; } = null!;
}
