using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SalesInvoiceItem
{
    public int InvoiceItemId { get; set; }

    public int InvoiceId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal DiscountPercent { get; set; }

    public decimal TaxPercent { get; set; }

    public decimal UnitCost { get; set; }

    public decimal LineTotal { get; set; }

    public virtual SalesInvoice Invoice { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
