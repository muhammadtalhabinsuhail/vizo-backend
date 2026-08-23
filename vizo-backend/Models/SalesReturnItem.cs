using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class SalesReturnItem
{
    public int ReturnItemId { get; set; }

    public int ReturnId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public int ConditionId { get; set; }

    public int? RestockLocationId { get; set; }

    public virtual ReturnCondition Condition { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;

    public virtual Location? RestockLocation { get; set; }

    public virtual SalesReturn Return { get; set; } = null!;
}
