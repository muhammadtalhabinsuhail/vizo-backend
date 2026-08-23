using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class OrderStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public int SortOrder { get; set; }

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();
}
