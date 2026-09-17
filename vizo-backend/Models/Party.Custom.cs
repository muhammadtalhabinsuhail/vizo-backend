namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// Party.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here.
/// Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/18_sales_scope_returns_and_places.sql.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition.
/// </summary>
public partial class Party
{
    /// <summary>
    /// Who typed this account in. Written once, never changed.
    ///
    /// NOT the same fact as <see cref="SalesPersonUserId"/>, which is the rep
    /// the relationship is ASSIGNED to -- that one is freely reassigned when a
    /// territory moves, and it is often left empty. Using it to answer "whose
    /// customers are these" means a rep loses their own account the day the
    /// owner hands it to somebody else.
    ///
    /// A salesperson's customer list is "created by me OR assigned to me", so
    /// both facts are used and neither is asked to do the other's job. The
    /// customer PICKER on the order form is not filtered at all -- a rep may
    /// sell to anybody, they simply do not administer everybody.
    ///
    /// Null on rows created before this column where no rep was set, which is
    /// the honest answer: nothing recorded it.
    /// </summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>The staff account that opened this party, if it is known.</summary>
    public virtual User? CreatedByUser { get; set; }
}
