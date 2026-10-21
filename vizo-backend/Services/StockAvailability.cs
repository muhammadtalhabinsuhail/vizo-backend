using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Services;

/// <summary>
/// How much of an item there is, and how much of it can still be promised.
///
/// The owner's 21 Oct list asked the same question in three places:
///
///   * CONFIRM ORDER -- accounts sees each item's quantity at Shop 2, the
///     Warehouse and the Order Department beside what was ordered, and the
///     order cannot be confirmed unless the ORDER DEPARTMENT can cover it
///     (that is where it will be packed from).
///   * NEW ORDER -- a salesperson is told the most they can order of an item,
///     in pieces, and cannot type more. They are NOT told the stock itself.
///   * DISPATCH -- the order desk sees what the chosen place holds, and a
///     place that cannot cover the order is refused.
///
/// One class so the three cannot disagree about what "available" means.
///
/// ─────────────────────────── THE TWO NUMBERS ───────────────────────────────
///
/// ON HAND is the shelf ("StockBalance") at the active places goods are sold
/// from -- warehouses, the order department and the shops. Claim Stock is
/// never counted (ExcludeFromSellable), and neither is a closed place.
///
/// MAX ORDERABLE is on hand across those places MINUS what other open orders
/// have already asked for and not yet taken (submitted, confirmed, invoiced,
/// processing, or held for the limit). Stock only leaves the shelf at Dispatch,
/// so without this two reps could each be promised the same last ten pieces.
/// It is a figure about promises, not about shelves -- which is why it is the
/// one a rep may see. Checked live on 21 Oct: it differs from plain stock on
/// one product in 35, so it does not choke ordinary selling.
/// </summary>
public static class StockAvailability
{
    /// <summary>LocationKind.KindKey of the order department.</summary>
    public const string DepartmentKind = "department";

    /// <summary>Orders whose goods are promised but still on a shelf.</summary>
    public static readonly string[] OpenStatuses =
    {
        OrderWorkflow.Submitted, OrderWorkflow.Confirmed, OrderWorkflow.Invoiced,
        OrderWorkflow.AtOrderDept, OrderWorkflow.CreditHold
    };

    /// <summary>
    /// The active places goods are sold from, shops first, then warehouses,
    /// then the order department -- the order the owner named them in.
    /// </summary>
    public static async Task<List<SellablePlace>> PlacesAsync(AppDbContext db) =>
        await db.Locations.AsNoTracking()
            .Where(l => l.IsActive && !l.ExcludeFromSellable)
            .OrderBy(l => l.Kind.KindKey == "shop" ? 0 : l.Kind.KindKey == "warehouse" ? 1 : 2)
            .ThenBy(l => l.LocationName)
            .Select(l => new SellablePlace(l.LocationId, l.LocationName, l.Kind.KindKey))
            .ToListAsync();

    /// <summary>
    /// On hand, per product per place, for the given products at the given
    /// places. A product with no balance row at a place is simply 0 there.
    /// </summary>
    public static async Task<Dictionary<(int ProductId, int LocationId), int>> OnHandAsync(
        AppDbContext db, IReadOnlyCollection<int> productIds, IReadOnlyCollection<int> locationIds)
    {
        var rows = await db.StockBalances.AsNoTracking()
            .Where(b => productIds.Contains(b.ProductId) && locationIds.Contains(b.LocationId))
            .Select(b => new { b.ProductId, b.LocationId, b.Quantity })
            .ToListAsync();
        return rows.ToDictionary(r => (r.ProductId, r.LocationId), r => r.Quantity);
    }

    /// <summary>
    /// The most of each product that may still be ordered. Never negative.
    /// <paramref name="exceptOrderId"/> leaves one order out of the promises --
    /// the order being edited must not count against itself.
    /// </summary>
    public static async Task<Dictionary<int, int>> MaxOrderableAsync(
        AppDbContext db, IReadOnlyCollection<int>? productIds = null, int? exceptOrderId = null)
    {
        var onHand = await db.StockBalances.AsNoTracking()
            .Where(b => b.Location.IsActive && !b.Location.ExcludeFromSellable)
            .Where(b => productIds == null || productIds.Contains(b.ProductId))
            .GroupBy(b => b.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(b => b.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty);

        var promised = await db.SalesOrderItems.AsNoTracking()
            .Where(i => OpenStatuses.Contains(i.Order.Status.StatusKey))
            .Where(i => exceptOrderId == null || i.OrderId != exceptOrderId)
            .Where(i => productIds == null || productIds.Contains(i.ProductId))
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Qty = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(x => x.ProductId, x => x.Qty);

        var ids = productIds ?? onHand.Keys.Union(promised.Keys).ToList();
        return ids.Distinct().ToDictionary(
            id => id,
            id => Math.Max(0, onHand.GetValueOrDefault(id) - promised.GetValueOrDefault(id)));
    }
}

/// <summary>A place goods can be sold or sent from.</summary>
public record SellablePlace(int Id, string Name, string Kind);
