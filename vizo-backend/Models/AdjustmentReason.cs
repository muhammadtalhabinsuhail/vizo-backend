using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class AdjustmentReason
{
    public int ReasonId { get; set; }

    public string ReasonKey { get; set; } = null!;

    public string ReasonName { get; set; } = null!;

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();
}
