using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- the tables backend/database/26_purchase_pricing_and_batches.sql
/// created. Mapping is by data annotations on the classes in PurchasePricing.Custom.cs,
/// so nothing here touches OnModelCreatingPartial.
/// </summary>
public partial class AppDbContext
{
    public virtual DbSet<StockBatch> StockBatches { get; set; } = null!;
    public virtual DbSet<StockBatchBalance> StockBatchBalances { get; set; } = null!;
    public virtual DbSet<StockBatchMovement> StockBatchMovements { get; set; } = null!;
    public virtual DbSet<PurchaseOrderEntry> PurchaseOrderEntries { get; set; } = null!;
}
