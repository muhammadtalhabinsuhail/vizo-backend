using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Claim
{
    public int ClaimId { get; set; }

    public string ClaimNo { get; set; } = null!;

    public int CustomerUserId { get; set; }

    public DateOnly ReceivedOn { get; set; }

    public int ReceivedByUserId { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public decimal UnitCost { get; set; }

    public int ReasonId { get; set; }

    public string? ClaimNote { get; set; }

    public string? OriginalOrderNo { get; set; }

    public int OutcomeId { get; set; }

    public int StageId { get; set; }

    public int? SupplierUserId { get; set; }

    public DateOnly? SentOn { get; set; }

    public DateOnly? SettledOn { get; set; }

    public string? SupplierNote { get; set; }

    public short RemindersSent { get; set; }

    public virtual Party CustomerUser { get; set; } = null!;

    public virtual ClaimOutcome Outcome { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;

    public virtual ClaimReason Reason { get; set; } = null!;

    public virtual Employee ReceivedByUser { get; set; } = null!;

    public virtual ClaimStage Stage { get; set; } = null!;

    public virtual Party? SupplierUser { get; set; }
}
