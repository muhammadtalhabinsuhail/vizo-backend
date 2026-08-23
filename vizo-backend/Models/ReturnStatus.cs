using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ReturnStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public virtual ICollection<PurchaseReturn> PurchaseReturns { get; set; } = new List<PurchaseReturn>();

    public virtual ICollection<SalesReturn> SalesReturns { get; set; } = new List<SalesReturn>();
}
