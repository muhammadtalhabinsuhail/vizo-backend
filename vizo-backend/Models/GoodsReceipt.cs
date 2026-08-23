using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class GoodsReceipt
{
    public int GrnId { get; set; }

    public string GrnNo { get; set; } = null!;

    public int? PoId { get; set; }

    public int SupplierUserId { get; set; }

    public int LocationId { get; set; }

    public DateOnly ReceiptDate { get; set; }

    public string DeliveryNoteNo { get; set; } = null!;

    public string? VehicleNo { get; set; }

    public decimal TotalValue { get; set; }

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int ReceivedByUserId { get; set; }

    public string? Notes { get; set; }

    public virtual JournalEntry? Entry { get; set; }

    public virtual ICollection<GoodsReceiptItem> GoodsReceiptItems { get; set; } = new List<GoodsReceiptItem>();

    public virtual Location Location { get; set; } = null!;

    public virtual PurchaseOrder? Po { get; set; }

    public virtual Employee ReceivedByUser { get; set; } = null!;

    public virtual PostingStatus Status { get; set; } = null!;

    public virtual Party SupplierUser { get; set; } = null!;
}
