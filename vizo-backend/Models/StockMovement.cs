using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockMovement
{
    public int MovementId { get; set; }

    public int ProductId { get; set; }

    public int LocationId { get; set; }

    public int MovementTypeId { get; set; }

    public DateTime MovedAt { get; set; }

    public string ReferenceNo { get; set; } = null!;

    public int Quantity { get; set; }

    public int BalanceAfter { get; set; }

    public int UserId { get; set; }

    public virtual Location Location { get; set; } = null!;

    public virtual MovementType MovementType { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
