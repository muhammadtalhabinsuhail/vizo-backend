using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SalesOrder
{
    public int OrderId { get; set; }

    public string OrderNo { get; set; } = null!;

    public int CustomerUserId { get; set; }

    public int LocationId { get; set; }

    public int? SalesPersonUserId { get; set; }

    public DateOnly OrderDate { get; set; }

    public DateOnly? DeliveryDate { get; set; }

    public int StatusId { get; set; }

    public int MethodId { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? CreditHoldReason { get; set; }

    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }

    public DateOnly CreatedAt { get; set; }

    public virtual ICollection<CollectionAllocation> CollectionAllocations { get; set; } = new List<CollectionAllocation>();

    public virtual User CreatedByUser { get; set; } = null!;

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    public virtual Location Location { get; set; } = null!;

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual SalesInvoice? SalesInvoice { get; set; }

    public virtual ICollection<SalesOrderItem> SalesOrderItems { get; set; } = new List<SalesOrderItem>();

    public virtual Employee? SalesPersonUser { get; set; }

    public virtual OrderStatus Status { get; set; } = null!;
}
