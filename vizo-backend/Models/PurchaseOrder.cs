using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseOrder
{
    public int PoId { get; set; }

    public string PoNo { get; set; } = null!;

    public int SupplierUserId { get; set; }

    public int LocationId { get; set; }

    public DateOnly PoDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public int StatusId { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }

    public int? ApprovedByUserId { get; set; }

    public virtual Employee? ApprovedByUser { get; set; }

    public virtual Employee CreatedByUser { get; set; } = null!;

    public virtual ICollection<GoodsReceipt> GoodsReceipts { get; set; } = new List<GoodsReceipt>();

    public virtual Location Location { get; set; } = null!;

    public virtual ICollection<PurchaseInvoice> PurchaseInvoices { get; set; } = new List<PurchaseInvoice>();

    public virtual ICollection<PurchaseOrderItem> PurchaseOrderItems { get; set; } = new List<PurchaseOrderItem>();

    public virtual PurchaseOrderStatus Status { get; set; } = null!;

    public virtual Party SupplierUser { get; set; } = null!;
}
