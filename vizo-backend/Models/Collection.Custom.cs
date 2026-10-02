namespace vizo_backend.Models;

public partial class Collection
{
    /// <summary>
    /// The Cash &amp; Bank account the money was received into, picked from the
    /// chart on the Collect modal (database/43_collection_deposit_account.sql).
    /// NULL on older collections, which post through their method instead --
    /// see LedgerPosting.PostCollectionAsync. A plain column, not a navigation,
    /// so the scaffolded Collection.cs and Account.cs stay untouched.
    /// </summary>
    public int? DepositAccountId { get; set; }
}
