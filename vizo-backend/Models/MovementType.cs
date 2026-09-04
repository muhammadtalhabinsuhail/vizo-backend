using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class MovementType
{
    public int MovementTypeId { get; set; }

    public string TypeKey { get; set; } = null!;

    public string TypeName { get; set; } = null!;

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();
}
