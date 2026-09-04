using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PurchaseReturnItem
{
    public int PrItemId { get; set; }

    public int PrId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public virtual PurchaseReturn Pr { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
