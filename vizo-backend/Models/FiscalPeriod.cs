using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class FiscalPeriod
{
    public int PeriodId { get; set; }

    public string PeriodName { get; set; } = null!;

    public short PeriodYear { get; set; }

    public short PeriodMonth { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly EndDate { get; set; }

    public bool IsClosed { get; set; }

    public int? ClosedByUserId { get; set; }

    public DateOnly? ClosedAt { get; set; }

    public virtual User? ClosedByUser { get; set; }

    public virtual ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();
}
