using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class PartyCategory
{
    public int CategoryId { get; set; }

    public string CategoryKey { get; set; } = null!;

    public string CategoryName { get; set; } = null!;

    public virtual ICollection<Party> Parties { get; set; } = new List<Party>();
}
