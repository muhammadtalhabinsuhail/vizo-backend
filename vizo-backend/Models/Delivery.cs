using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Delivery
{
    public int DeliveryId { get; set; }

    public string DeliveryNo { get; set; } = null!;

    public int OrderId { get; set; }

    public int? InvoiceId { get; set; }

    public int ChannelId { get; set; }

    public int? CourierId { get; set; }

    public string? TrackingNo { get; set; }

    public DateOnly BookedDate { get; set; }

    public DateOnly? ExpectedDate { get; set; }

    public DateOnly? DeliveredDate { get; set; }

    public int StatusId { get; set; }

    public int Parcels { get; set; }

    public decimal WeightKg { get; set; }

    public decimal CodAmount { get; set; }

    public bool IsCodSettled { get; set; }

    public decimal BookingCharge { get; set; }

    public short RemindersSent { get; set; }

    public int? ConfirmedByUserId { get; set; }

    public string? Notes { get; set; }

    public virtual DeliveryChannel Channel { get; set; } = null!;

    public virtual Employee? ConfirmedByUser { get; set; }

    public virtual Courier? Courier { get; set; }

    public virtual SalesInvoice? Invoice { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual DeliveryStatus Status { get; set; } = null!;
}
