using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Courier
{
    public int CourierId { get; set; }

    public string CourierName { get; set; } = null!;

    public string ShortName { get; set; } = null!;

    public string? ContactPerson { get; set; }

    public string? Phone { get; set; }

    public short CodSettlementDays { get; set; }

    public decimal BookingCharge { get; set; }

    public decimal CodFeePercent { get; set; }

    public string? TrackingUrlTemplate { get; set; }

    public bool IsActive { get; set; }

    public virtual ICollection<Delivery> Deliveries { get; set; } = new List<Delivery>();

    public virtual ICollection<DeliveryChannel> Channels { get; set; } = new List<DeliveryChannel>();
}
