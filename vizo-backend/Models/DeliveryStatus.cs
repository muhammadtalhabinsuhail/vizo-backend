using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class DeliveryStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public bool IsOpen { get; set; }

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();
}
