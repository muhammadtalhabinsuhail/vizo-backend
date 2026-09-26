using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace vizo_backend.Models;

/*
 * HAND-WRITTEN -- the columns and tables backend/database/26_purchase_pricing_and_batches.sql
 * added. Mapped with data annotations, not in AppDbContext.Custom.cs, so three
 * sessions working in parallel on 26 Sep could each add tables without all
 * editing the one OnModelCreatingPartial. The DbSets are in AppDbContext.Purchases.cs.
 *
 * AFTER A RE-SCAFFOLD: the new columns on Product / PurchaseOrder /
 * PurchaseOrderItem will be generated into the scaffolded files, so delete the
 * three partial blocks at the top of this file; the four new tables will also
 * be generated -- delete their classes below too.
 */

public partial class Product
{
    /// <summary>
    /// Fi Sabilillah: the part of the selling price set aside for the cause, PKR per unit.
    /// The owner's fourth price box; never negative.
    /// </summary>
    [Precision(14, 2)]
    public decimal FsPrice { get; set; }

    /// <summary>
    /// Margin 2: a second amount on top, for a reason the admin writes down when he
    /// uses it (Margin 1 is the ordinary margin and lives in <see cref="MarginPrice"/>).
    /// </summary>
    [Precision(14, 2)]
    public decimal Margin2Price { get; set; }
}

public partial class PurchaseOrder
{
    /// <summary>The supplier's own bill number, if the admin has it. Optional.</summary>
    [MaxLength(50)]
    public string? SupplierBillNo { get; set; }

    public virtual ICollection<PurchaseOrderEntry> PurchaseOrderEntries { get; set; } = new List<PurchaseOrderEntry>();
}

public partial class PurchaseOrderItem
{
    /* The five price boxes of a purchase line, saved exactly as entered. UnitCost
       (scaffolded) is the first; these are the other four, each PKR per unit. */
    [Precision(14, 2)] public decimal DutyPrice { get; set; }
    [Precision(14, 2)] public decimal FsPrice { get; set; }
    [Precision(14, 2)] public decimal Margin1Price { get; set; }
    [Precision(14, 2)] public decimal Margin2Price { get; set; }

    /// <summary>The logistics company this line's duty is owed to (a child of account 2150).</summary>
    public int? DutyAccountId { get; set; }

    [ForeignKey(nameof(DutyAccountId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public virtual Account? DutyAccount { get; set; }

    /* Why each extra box holds what it holds. Printed on the journal voucher. */
    [MaxLength(300)] public string? DutyNote { get; set; }
    [MaxLength(300)] public string? FsNote { get; set; }
    [MaxLength(300)] public string? Margin1Note { get; set; }
    [MaxLength(300)] public string? Margin2Note { get; set; }

    /// <summary>The selling price this line was bought to sell at: the five parts summed.</summary>
    [NotMapped]
    public decimal UnitSalePrice => UnitCost + DutyPrice + FsPrice + Margin1Price + Margin2Price;
}

/// <summary>
/// One lot of one product: a purchase-order line, or the product's OPENING lot
/// (everything on the shelves on the day lots began, 26 Sep 2026).
/// </summary>
[Table("StockBatch")]
public class StockBatch
{
    [Key] public int BatchId { get; set; }
    public int ProductId { get; set; }
    public int? PoItemId { get; set; }
    [MaxLength(30)] public string BatchNo { get; set; } = null!;
    public DateOnly BatchDate { get; set; }
    public int QtyReceived { get; set; }
    [Precision(14, 2)] public decimal UnitCost { get; set; }
    [Precision(14, 2)] public decimal UnitDuty { get; set; }
    [Precision(14, 2)] public decimal UnitFs { get; set; }
    [Precision(14, 2)] public decimal UnitMargin1 { get; set; }
    [Precision(14, 2)] public decimal UnitMargin2 { get; set; }
    [Column(TypeName = "timestamp without time zone")] public DateTime CreatedAt { get; set; }

    [NotMapped]
    public decimal UnitSalePrice => UnitCost + UnitDuty + UnitFs + UnitMargin1 + UnitMargin2;

    [ForeignKey(nameof(ProductId))] public virtual Product Product { get; set; } = null!;
    [ForeignKey(nameof(PoItemId))] public virtual PurchaseOrderItem? PoItem { get; set; }
    public virtual ICollection<StockBatchBalance> Balances { get; set; } = new List<StockBatchBalance>();
}

/// <summary>How many of a lot sit at one location. Sums to StockBalance per product/location.</summary>
[Table("StockBatchBalance")]
[PrimaryKey(nameof(BatchId), nameof(LocationId))]
public class StockBatchBalance
{
    public int BatchId { get; set; }
    public int LocationId { get; set; }
    public int Quantity { get; set; }

    [ForeignKey(nameof(BatchId))] public virtual StockBatch Batch { get; set; } = null!;
    [ForeignKey(nameof(LocationId))] public virtual Location Location { get; set; } = null!;
}

/// <summary>Which lots one stock movement touched, and by how much (signed like the movement).</summary>
[Table("StockBatchMovement")]
[PrimaryKey(nameof(MovementId), nameof(BatchId))]
public class StockBatchMovement
{
    public int MovementId { get; set; }
    public int BatchId { get; set; }
    public int Quantity { get; set; }

    [ForeignKey(nameof(MovementId))] public virtual StockMovement Movement { get; set; } = null!;
    [ForeignKey(nameof(BatchId))] public virtual StockBatch Batch { get; set; } = null!;
}

/// <summary>A journal voucher a purchase order wrote, and which price part it was for.</summary>
[Table("PurchaseOrderEntry")]
[PrimaryKey(nameof(PoId), nameof(EntryId))]
public class PurchaseOrderEntry
{
    public int PoId { get; set; }
    public int EntryId { get; set; }

    /// <summary>GOODS, DUTY, FS, MARGIN1 or MARGIN2.</summary>
    [MaxLength(10)] public string Component { get; set; } = null!;

    [ForeignKey(nameof(PoId))] public virtual PurchaseOrder Po { get; set; } = null!;
    [ForeignKey(nameof(EntryId))] public virtual JournalEntry Entry { get; set; } = null!;
}
