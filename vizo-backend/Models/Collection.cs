using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Collection
{
    public int CollectionId { get; set; }

    public string ReceiptNo { get; set; } = null!;

    public int CustomerUserId { get; set; }

    public int CollectedByUserId { get; set; }

    public DateOnly CollectedOn { get; set; }

    public decimal Amount { get; set; }

    public int MethodId { get; set; }

    public string? ReferenceNo { get; set; }

    public string? BankName { get; set; }

    public DateOnly? ChequeDate { get; set; }

    public int StatusId { get; set; }

    public DateOnly? ConfirmedOn { get; set; }

    public int? ConfirmedByUserId { get; set; }

    public int? VoucherId { get; set; }

    public string? Note { get; set; }

    public virtual Employee CollectedByUser { get; set; } = null!;

    public virtual ICollection<CollectionAllocation> CollectionAllocations { get; set; } = new List<CollectionAllocation>();

    public virtual Employee? ConfirmedByUser { get; set; }

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual PaymentMethod Method { get; set; } = null!;

    public virtual CollectionStatus Status { get; set; } = null!;

    public virtual Voucher? Voucher { get; set; }
}
