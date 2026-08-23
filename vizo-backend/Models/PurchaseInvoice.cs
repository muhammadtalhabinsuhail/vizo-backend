using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseInvoice
{
    public int PiId { get; set; }

    public string InvoiceNo { get; set; } = null!;

    public string SupplierInvoiceNo { get; set; } = null!;

    public int SupplierUserId { get; set; }

    public int? PoId { get; set; }

    public DateOnly InvoiceDate { get; set; }

    public DateOnly DueDate { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal WhtAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public int StatusId { get; set; }

    public int MethodId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual Employee CreatedByUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual PurchaseOrder? Po { get; set; }

    public virtual ICollection<PurchaseInvoiceItem> PurchaseInvoiceItems { get; set; } = new List<PurchaseInvoiceItem>();

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual InvoiceStatus Status { get; set; } = null!;

    public virtual Party SupplierUser { get; set; } = null!;

    public virtual ICollection<VoucherAllocation> VoucherAllocations { get; set; } = new List<VoucherAllocation>();
}
