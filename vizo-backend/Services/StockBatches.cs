using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// Keeps the stock LOTS in step with the shelf.
///
/// WHY THIS EXISTS. The owner (26 Sep): "hamein pata ho ke yeh wali jo quantity
/// hai, yeh kaun se purchase order ki hai" -- every unit, wherever it sits, must
/// be traceable to the purchase order it came from, because a new purchase's
/// selling price is set by averaging over what is left of the old ones. The
/// shelf itself ("StockBalance") is one number per product per place and cannot
/// say that, so each unit also lives in a lot ("StockBatch"): a PO line, or the
/// product's OPENING lot for what was already on the shelves on 26 Sep.
///
/// THE ONE RULE: for every product and place, the lots add up to the shelf.
/// Every code path that changes "StockBalance" writes its "StockMovement"
/// through <see cref="RecordAsync"/> instead of adding it directly, and this
/// class moves the same quantity between lots and links the movement to them
/// ("StockBatchMovement"), in the same unit of work. Nothing else writes lots,
/// except the purchase order itself, which creates its own.
///
/// WHICH LOTS. Going out, the oldest lot first (FIFO) -- the owner's example
/// averages the July stock that is "still in the godown" with September's, so
/// the old stock is what sells first. Coming back in (a transfer arriving, a
/// customer returning goods, a rejected return being undone) it goes back to
/// the lots the matching earlier movement took from, so a transfer never turns
/// July stock into September stock on the way. Stock that simply appears (a
/// count found more than the shelf said) joins the newest lot.
///
/// SHORT SHELVES. The shelf is allowed to go negative today (two rows were,
/// on the day lots began). Rather than refuse the sale, the unmatched part is
/// taken from the newest lot, which then goes negative too -- the invariant
/// holds and the next purchase fills it.
/// </summary>
public static class StockBatches
{
    /// <summary>A quantity of one lot. The lot is an object, not an id, so a lot
    /// created earlier in the same unit of work can be used before it is saved.</summary>
    public sealed record Part(StockBatch Batch, int Qty);

    /// <summary>
    /// Adds <paramref name="mv"/> and moves the same quantity between lots.
    ///
    /// <paramref name="followRef"/>: the reference number of an earlier movement
    /// this one undoes or completes (the transfer number, the order a return is
    /// against, the return being rejected). Its lots are used first.
    /// <paramref name="into"/>: the lot an incoming movement belongs to, when the
    /// caller knows it (a purchase order creating its own lot).
    /// </summary>
    public static async Task RecordAsync(AppDbContext db, StockMovement mv,
        string? followRef = null, StockBatch? into = null)
    {
        db.StockMovements.Add(mv);
        if (mv.Quantity == 0) return;

        List<Part> parts;
        if (mv.Quantity < 0)
        {
            var prefer = followRef is null ? null
                : await EarlierPartsAsync(db, followRef, mv.ProductId, positive: true);
            parts = await TakeAsync(db, mv.ProductId, mv.LocationId, -mv.Quantity, prefer);
        }
        else if (into is not null)
        {
            (await BalanceRowAsync(db, into, mv.LocationId)).Quantity += mv.Quantity;
            parts = new() { new Part(into, mv.Quantity) };
        }
        else
        {
            var like = followRef is null ? null
                : await EarlierPartsAsync(db, followRef, mv.ProductId, positive: false);
            parts = await PutAsync(db, mv.ProductId, mv.LocationId, mv.Quantity, like);
        }

        var sign = Math.Sign(mv.Quantity);
        foreach (var p in parts.Where(p => p.Qty != 0))
            db.StockBatchMovements.Add(new StockBatchMovement { Movement = mv, Batch = p.Batch, Quantity = sign * p.Qty });
    }

    /// <summary>A fresh lot for a purchase-order line. The caller adds the movement
    /// with <c>into:</c> this lot.</summary>
    public static StockBatch NewLot(AppDbContext db, PurchaseOrderItem line, string poNo, DateOnly date, DateTime now)
    {
        var lot = new StockBatch
        {
            ProductId = line.ProductId,
            PoItem = line,
            BatchNo = poNo,
            BatchDate = date,
            QtyReceived = line.Quantity,
            UnitCost = line.UnitCost,
            UnitDuty = line.DutyPrice,
            UnitFs = line.FsPrice,
            UnitMargin1 = line.Margin1Price,
            UnitMargin2 = line.Margin2Price,
            CreatedAt = now
        };
        db.StockBatches.Add(lot);
        return lot;
    }

    // ───────────────────────────── internals ─────────────────────────────

    private static async Task<List<Part>> TakeAsync(AppDbContext db, int productId, int locationId,
        int qty, List<Part>? prefer)
    {
        var rows = await RowsAtAsync(db, productId, locationId);
        var preferIds = prefer?.Select(p => p.Batch).Distinct().ToList() ?? new();

        var ordered = rows
            .OrderBy(r => preferIds.IndexOf(r.Batch) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(r => r.Batch.BatchDate).ThenBy(r => r.Batch.BatchId)
            .ToList();

        var taken = new List<Part>();
        var left = qty;
        foreach (var r in ordered)
        {
            if (left == 0) break;
            if (r.Quantity <= 0) continue;
            var t = Math.Min(r.Quantity, left);
            r.Quantity -= t;
            left -= t;
            taken.Add(new Part(r.Batch, t));
        }

        if (left > 0)
        {
            /* The shelf was short. The newest lot here carries the shortfall; if
               there is no lot here at all, the product's newest lot anywhere. */
            var newest = rows.OrderByDescending(r => r.Batch.BatchDate).ThenByDescending(r => r.Batch.BatchId)
                             .FirstOrDefault()?.Batch
                         ?? await NewestLotAsync(db, productId);
            (await BalanceRowAsync(db, newest, locationId)).Quantity -= left;
            taken.Add(new Part(newest, left));
        }
        return Merge(taken);
    }

    private static async Task<List<Part>> PutAsync(AppDbContext db, int productId, int locationId,
        int qty, List<Part>? like)
    {
        var put = new List<Part>();
        var left = qty;
        foreach (var p in like ?? new())
        {
            if (left == 0) break;
            var t = Math.Min(p.Qty, left);
            if (t <= 0) continue;
            (await BalanceRowAsync(db, p.Batch, locationId)).Quantity += t;
            put.Add(new Part(p.Batch, t));
            left -= t;
        }
        if (left > 0)
        {
            var newest = await NewestLotAsync(db, productId);
            (await BalanceRowAsync(db, newest, locationId)).Quantity += left;
            put.Add(new Part(newest, left));
        }
        return Merge(put);
    }

    /// <summary>The lots earlier movements with this reference took (positive=false)
    /// or put (positive=true), as positive quantities, newest movement first.</summary>
    private static async Task<List<Part>> EarlierPartsAsync(AppDbContext db, string reference,
        int productId, bool positive)
    {
        var rows = await db.StockBatchMovements
            .Include(m => m.Batch)
            .Where(m => m.Movement.ReferenceNo == reference && m.Movement.ProductId == productId
                        && (positive ? m.Quantity > 0 : m.Quantity < 0))
            .OrderByDescending(m => m.MovementId)
            .ToListAsync();
        return Merge(rows.Select(r => new Part(r.Batch, Math.Abs(r.Quantity))).ToList());
    }

    /// <summary>Every lot row of this product at this place -- saved ones and ones
    /// added earlier in this same unit of work.</summary>
    private static async Task<List<StockBatchBalance>> RowsAtAsync(AppDbContext db, int productId, int locationId)
    {
        var saved = await db.StockBatchBalances
            .Include(b => b.Batch)
            .Where(b => b.LocationId == locationId && b.Batch.ProductId == productId)
            .ToListAsync();
        var added = db.StockBatchBalances.Local
            .Where(b => b.LocationId == locationId && b.Batch != null && b.Batch.ProductId == productId
                        && !saved.Contains(b));
        return saved.Concat(added).ToList();
    }

    private static async Task<StockBatchBalance> BalanceRowAsync(AppDbContext db, StockBatch lot, int locationId)
    {
        var local = db.StockBatchBalances.Local
            .FirstOrDefault(b => b.LocationId == locationId && ReferenceEquals(b.Batch, lot));
        if (local is not null) return local;

        if (lot.BatchId != 0)
        {
            var saved = await db.StockBatchBalances
                .FirstOrDefaultAsync(b => b.BatchId == lot.BatchId && b.LocationId == locationId);
            if (saved is not null) return saved;
        }

        var row = new StockBatchBalance { Batch = lot, LocationId = locationId, Quantity = 0 };
        db.StockBatchBalances.Add(row);
        return row;
    }

    /// <summary>The product's newest lot. A product that has never had one (added
    /// after 26 Sep and never bought through a purchase order) gets an empty
    /// OPENING lot at its own current price, so there is always somewhere to
    /// book a unit.</summary>
    private static async Task<StockBatch> NewestLotAsync(AppDbContext db, int productId)
    {
        var local = db.StockBatches.Local.Where(b => b.ProductId == productId || b.PoItem?.ProductId == productId)
            .OrderByDescending(b => b.BatchDate).ThenByDescending(b => b.BatchId == 0 ? int.MaxValue : b.BatchId)
            .FirstOrDefault();
        if (local is not null) return local;

        var lot = await db.StockBatches
            .Where(b => b.ProductId == productId)
            .OrderByDescending(b => b.BatchDate).ThenByDescending(b => b.BatchId)
            .FirstOrDefaultAsync();
        if (lot is not null) return lot;

        var p = await db.Products.FirstAsync(x => x.ProductId == productId);
        lot = new StockBatch
        {
            ProductId = productId,
            BatchNo = "OPENING",
            BatchDate = BusinessClock.Today(),
            QtyReceived = 0,
            UnitCost = p.CostPrice,
            UnitDuty = p.DutyPrice,
            UnitFs = p.FsPrice,
            UnitMargin1 = p.MarginPrice,
            UnitMargin2 = p.Margin2Price,
            CreatedAt = BusinessClock.Now()
        };
        db.StockBatches.Add(lot);
        return lot;
    }

    private static List<Part> Merge(List<Part> parts) =>
        parts.GroupBy(p => p.Batch, ReferenceEqualityComparer.Instance)
             .Select(g => new Part((StockBatch)g.Key!, g.Sum(x => x.Qty)))
             .ToList();
}
