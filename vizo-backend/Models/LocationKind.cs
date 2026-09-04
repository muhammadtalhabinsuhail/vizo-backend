using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class LocationKind
{
    public int KindId { get; set; }

    public string KindKey { get; set; } = null!;

    public string KindName { get; set; } = null!;

    public virtual ICollection<Location> Locations { get; set; } = new List<Location>();
}
