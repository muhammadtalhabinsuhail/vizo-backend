namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// Product.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the columns this project added live here.
/// Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/19_product_pricing.sql.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition -- both columns exist on Neon now and a fresh scaffold generates
/// them inside Product.cs itself.
/// </summary>
public partial class Product
{
    /// <summary>
    /// Duty, clearing and whatever else is paid on top of the supplier's price
    /// to get one unit through the door, in PKR.
    ///
    /// Landed cost is <see cref="CostPrice"/> + this.
    /// </summary>
    public decimal DutyPrice { get; set; }

    /// <summary>
    /// What is added to the landed cost to arrive at <see cref="SalePrice"/>,
    /// in PKR per unit.
    ///
    /// The margin PERCENTAGE is deliberately not stored: it is
    /// MarginPrice / (CostPrice + DutyPrice), always derivable from the three
    /// prices on the row, and a percentage kept beside them is a second answer
    /// to a question the row already answers. SalePrice stays the authority --
    /// every invoice and report reads it and none should have to know how it
    /// was arrived at.
    /// </summary>
    public decimal MarginPrice { get; set; }

    /// <summary>Cost plus duty: what one unit really costs to have on the shelf.</summary>
    public decimal LandedCost => CostPrice + DutyPrice;
}
