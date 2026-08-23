using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class TransferStatus
{
    public int StatusId { get; set; }

    public string StatusKey { get; set; } = null!;

    public string StatusName { get; set; } = null!;

    public virtual ICollection<StockTransfer> StockTransfers { get; set; } = new List<StockTransfer>();
}
