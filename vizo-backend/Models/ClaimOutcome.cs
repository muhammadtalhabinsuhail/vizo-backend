using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ClaimOutcome
{
    public int OutcomeId { get; set; }

    public string OutcomeKey { get; set; } = null!;

    public string OutcomeName { get; set; } = null!;

    public virtual ICollection<Claim> Claims { get; set; } = new List<Claim>();
}
