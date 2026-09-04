using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Product
{
    public int ProductId { get; set; }

    public string Sku { get; set; } = null!;

    public string ProductName { get; set; } = null!;

    public string? Description { get; set; }

    public int CategoryId { get; set; }

    public int BrandId { get; set; }

    public int Packing { get; set; }

    public int MinQty { get; set; }

    public int MaxQty { get; set; }

    public decimal OpeningCost { get; set; }

    public decimal CostPrice { get; set; }

    public decimal SalePrice { get; set; }

    public decimal TaxRatePercent { get; set; }

    public bool HideStock { get; set; }

    public bool IsActive { get; set; }

    public string? ImageUrl { get; set; }

    public DateOnly CreatedAt { get; set; }

    public virtual Brand Brand { get; set; } = null!;

    public virtual Category Category { get; set; } = null!;

    public virtual ICollection<Claim> Claims { get; set; } = new List<Claim>();

    public virtual ICollection<GoodsReceiptItem> GoodsReceiptItems { get; set; } = new List<GoodsReceiptItem>();

    public virtual ICollection<ProductBarcode> ProductBarcodes { get; set; } = new List<ProductBarcode>();

    public virtual ICollection<PurchaseInvoiceItem> PurchaseInvoiceItems { get; set; } = new List<PurchaseInvoiceItem>();

    public virtual ICollection<PurchaseOrderItem> PurchaseOrderItems { get; set; } = new List<PurchaseOrderItem>();

    public virtual ICollection<PurchaseReturnItem> PurchaseReturnItems { get; set; } = new List<PurchaseReturnItem>();

    public virtual ICollection<SalesInvoiceItem> SalesInvoiceItems { get; set; } = new List<SalesInvoiceItem>();

    public virtual ICollection<SalesOrderItem> SalesOrderItems { get; set; } = new List<SalesOrderItem>();

    public virtual ICollection<SalesReturnItem> SalesReturnItems { get; set; } = new List<SalesReturnItem>();

    public virtual ICollection<StockAdjustmentItem> StockAdjustmentItems { get; set; } = new List<StockAdjustmentItem>();

    public virtual ICollection<StockBalance> StockBalances { get; set; } = new List<StockBalance>();

    public virtual ICollection<StockMovement> StockMovements { get; set; } = new List<StockMovement>();

    public virtual ICollection<StockTransferItem> StockTransferItems { get; set; } = new List<StockTransferItem>();
}
