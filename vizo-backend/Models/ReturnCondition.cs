using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ReturnCondition
{
    public int ConditionId { get; set; }

    public string ConditionKey { get; set; } = null!;

    public string ConditionName { get; set; } = null!;

    public bool IsResalable { get; set; }

    public virtual ICollection<SalesReturnItem> SalesReturnItems { get; set; } = new List<SalesReturnItem>();
}
