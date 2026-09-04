using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class ProductBarcode
{
    public int BarcodeId { get; set; }

    public int ProductId { get; set; }

    public string Barcode { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
