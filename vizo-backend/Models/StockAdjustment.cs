using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockAdjustment
{
    public int AdjustmentId { get; set; }

    public string AdjustmentNo { get; set; } = null!;

    public int LocationId { get; set; }

    public DateOnly AdjustmentDate { get; set; }

    public int ReasonId { get; set; }

    public string ReasonNotes { get; set; } = null!;

    public int StatusId { get; set; }

    public int? EntryId { get; set; }

    public int CreatedByUserId { get; set; }

    public virtual Employee CreatedByUser { get; set; } = null!;

    public virtual JournalEntry? Entry { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual AdjustmentReason Reason { get; set; } = null!;

    public virtual PostingStatus Status { get; set; } = null!;

    public virtual ICollection<StockAdjustmentItem> StockAdjustmentItems { get; set; } = new List<StockAdjustmentItem>();
}
