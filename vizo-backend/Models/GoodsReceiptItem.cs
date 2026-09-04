using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class GoodsReceiptItem
{
    public int GrnItemId { get; set; }

    public int GrnId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int QtyReceived { get; set; }

    public int QtyDamaged { get; set; }

    public decimal UnitCost { get; set; }

    public string? BatchNo { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public virtual GoodsReceipt Grn { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
