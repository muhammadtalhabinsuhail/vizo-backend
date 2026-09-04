using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class CustomerVisit
{
    public int VisitId { get; set; }

    public int CustomerUserId { get; set; }

    public int SalesPersonUserId { get; set; }

    public DateTime VisitedAt { get; set; }

    public int OutcomeId { get; set; }

    public string? Notes { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual VisitOutcome Outcome { get; set; } = null!;

    public virtual Employee SalesPersonUser { get; set; } = null!;
}
