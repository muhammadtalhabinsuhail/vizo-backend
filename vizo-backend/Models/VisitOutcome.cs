using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class VisitOutcome
{
    public int OutcomeId { get; set; }

    public string OutcomeKey { get; set; } = null!;

    public string OutcomeName { get; set; } = null!;

    public virtual ICollection<CustomerVisit> CustomerVisits { get; set; } = new List<CustomerVisit>();
}
