using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class CollectionStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public virtual ICollection<Collection> Collections { get; set; } = new List<Collection>();
}
