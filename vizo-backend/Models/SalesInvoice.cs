using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SalesInvoice
{
    public int InvoiceId { get; set; }

    public string InvoiceNo { get; set; } = null!;

    public int? OrderId { get; set; }

    public int CustomerUserId { get; set; }

    public int LocationId { get; set; }

    public DateOnly InvoiceDate { get; set; }

    public DateOnly DueDate { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public int StatusId { get; set; }

    public int MethodId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    /* Added by backend/database/08_sales_documents.sql. */

    /// <summary>Cloudinary secure_url of the rendered bill, or null until it is built.</summary>
    public string? PdfUrl { get; set; }

    /// <summary>Cloudinary public_id, kept so the asset can be replaced or removed.</summary>
    public string? PdfPublicId { get; set; }

    /// <summary>True for a counter sale to somebody with no shop account.</summary>
    public bool IsWalkIn { get; set; }

    /// <summary>The walk-in buyer's own name. Null on account sales.</summary>
    public string? WalkInName { get; set; }

    /// <summary>The walk-in buyer's own number -- this is what the WhatsApp share dials.</summary>
    public string? WalkInPhone { get; set; }

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    public virtual JournalEntry? Entry { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual SalesOrder? Order { get; set; }

    public virtual ICollection<SalesInvoiceItem> SalesInvoiceItems { get; set; } = new List<SalesInvoiceItem>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();

    public virtual InvoiceStatus Status { get; set; } = null!;

    public virtual ICollection<VoucherAllocation> VoucherAllocations { get; set; } = new List<VoucherAllocation>();
}
