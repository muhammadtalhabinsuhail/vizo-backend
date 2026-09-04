using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class VoucherAllocation
{
    public int AllocationId { get; set; }

    public int VoucherId { get; set; }

    public int? SalesInvoiceId { get; set; }

    public int? PurchaseInvoiceId { get; set; }

    public decimal Amount { get; set; }

    public virtual PurchaseInvoice? PurchaseInvoice { get; set; }

    public virtual SalesInvoice? SalesInvoice { get; set; }

    public virtual Voucher Voucher { get; set; } = null!;
}
