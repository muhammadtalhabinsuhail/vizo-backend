using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class DeliveryChannel
{
    public int ChannelId { get; set; }

    public string ChannelKey { get; set; } = null!;

    public string ChannelName { get; set; } = null!;

    public string Description { get; set; } = null!;

    public int ConfirmedByRoleId { get; set; }

    public short RemindAfterDays { get; set; }

    public short RemindEveryHours { get; set; }

    public bool AutoConfirm { get; set; }

    public bool RequiresBilty { get; set; }

    public bool IsActive { get; set; }

    public virtual Role ConfirmedByRole { get; set; } = null!;

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    public virtual ICollection<Courier> Couriers { get; set; } = new List<Courier>();
}
