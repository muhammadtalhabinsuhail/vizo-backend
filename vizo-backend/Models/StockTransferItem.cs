using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class StockTransferItem
{
    public int TransferItemId { get; set; }

    public int TransferId { get; set; }

    public short LineNo { get; set; }

    public int ProductId { get; set; }

    public int Quantity { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual StockTransfer Transfer { get; set; } = null!;
}
