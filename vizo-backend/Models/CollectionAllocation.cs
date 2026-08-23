using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class CollectionAllocation
{
    public int AllocationId { get; set; }

    public int CollectionId { get; set; }

    public int OrderId { get; set; }

    public decimal Amount { get; set; }

    public virtual Collection Collection { get; set; } = null!;

    public virtual SalesOrder Order { get; set; } = null!;
}
