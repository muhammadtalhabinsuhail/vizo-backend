using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockTransfer
{
    public int TransferId { get; set; }

    public string TransferNo { get; set; } = null!;

    public int FromLocationId { get; set; }

    public int ToLocationId { get; set; }

    public DateOnly TransferDate { get; set; }

    public int StatusId { get; set; }

    public int InitiatedByUserId { get; set; }

    public int? ApprovedByUserId { get; set; }

    public DateOnly? ReceivedOn { get; set; }

    public string? Notes { get; set; }

    public virtual Employee? ApprovedByUser { get; set; }

    public virtual Location FromLocation { get; set; } = null!;

    public virtual Employee InitiatedByUser { get; set; } = null!;

    public virtual TransferStatus Status { get; set; } = null!;

    public virtual ICollection<StockTransferItem> StockTransferItems { get; set; } = new List<StockTransferItem>();

    public virtual Location ToLocation { get; set; } = null!;
}
