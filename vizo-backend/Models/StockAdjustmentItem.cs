using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockAdjustmentItem
{
    public int AdjustmentItemId { get; set; }

    public int AdjustmentId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int CurrentQty { get; set; }

    public int NewQty { get; set; }

    public virtual StockAdjustment Adjustment { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
