using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseInvoiceItem
{
    public int PiItemId { get; set; }

    public int PiId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal TaxPercent { get; set; }

    public decimal LineTotal { get; set; }

    public virtual PurchaseInvoice Pi { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
