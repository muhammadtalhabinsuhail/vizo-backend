namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// PaymentMethod.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here.
/// Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/21_order_chain_and_receiving.sql.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition -- the column exists on Neon now and a fresh scaffold generates
/// it inside PaymentMethod.cs itself.
/// </summary>
public partial class PaymentMethod
{
    /// <summary>
    /// Whether this is a way money comes IN.
    ///
    /// The table has always been one list for both directions, so the screen
    /// that records a customer's payment offered Petty Cash and Credit Note
    /// beside Cash -- eight choices where the business has four, and every
    /// extra one is another way to file a receipt under the wrong heading.
    ///
    /// The owner's four are Cash, Credit, Meezan and Faysal. They are marked
    /// here rather than listed in the code so another bank can be added from
    /// the database without a deploy.
    ///
    /// Money going OUT -- an expense, a payment voucher, a refund -- still uses
    /// the whole table. Nothing was removed.
    /// </summary>
    public bool IsForReceiving { get; set; }
}
