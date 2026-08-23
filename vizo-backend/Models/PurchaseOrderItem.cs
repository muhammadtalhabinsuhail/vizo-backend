using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseOrderItem
{
    public int PoItemId { get; set; }

    public int PoId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal TaxPercent { get; set; }

    public decimal LineTotal { get; set; }

    public virtual PurchaseOrder Po { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
