using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class CreditHoldPolicy
{
    public int PolicyId { get; set; }

    public string PolicyKey { get; set; } = null!;

    public string PolicyName { get; set; } = null!;

    public string Description { get; set; } = null!;

    public virtual ICollection<Party> Parties { get; set; } = new List<Party>();
}
