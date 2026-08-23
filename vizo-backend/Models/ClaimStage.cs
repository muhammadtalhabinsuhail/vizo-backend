using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ClaimStage
{
    public int StageId { get; set; }

    public string StageKey { get; set; } = null!;

    public string StageName { get; set; } = null!;

    public bool IsOpen { get; set; }

    public virtual ICollection<Claim> Claims { get; set; } = new List<Claim>();
}
