using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockBalance
{
    public int ProductId { get; set; }

    public int LocationId { get; set; }

    public int Quantity { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
