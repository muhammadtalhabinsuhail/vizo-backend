using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ClaimReason
{
    public int ReasonId { get; set; }

    public string ReasonKey { get; set; } = null!;

    public string ReasonName { get; set; } = null!;

    public bool UsuallyAccepted { get; set; }

    public virtual ICollection<Claim> Claims { get; set; } = new List<Claim>();
}
