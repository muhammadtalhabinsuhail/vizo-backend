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

    /* ───────────────────── THE CUSTOMER'S DOCUMENTS ─────────────────────

       Six photographs taken when the account is opened, and the one PDF they
       are bound into. Added by database/22_customer_documents.sql.

       Every one of them is nullable and every one of them is empty on the 26
       parties that existed before: a shopkeeper with no affidavit -- or with
       none of the three -- is still a customer. The screen asks for each set
       in turn and "not available" is an answer.

       LINKS, NOT BYTES. The pictures live on the images Cloudinary account and
       the PDF on the documents one, the same as every other file this system
       makes. A photograph in a column that every party query reads would be
       felt on every screen that lists a customer.                          */

    /// <summary>CNIC, the side with the photograph.</summary>
    public string? CnicFrontUrl { get; set; }

    /// <summary>CNIC, the side with the address.</summary>
    public string? CnicBackUrl { get; set; }

    /// <summary>The shop's own business card -- where the trading name, the
    /// market and the shop's phone and email actually come from.</summary>
    public string? CardFrontUrl { get; set; }

    /// <summary>The back of the card, which often carries the address.</summary>
    public string? CardBackUrl { get; set; }

    /// <summary>The affidavit, first page.</summary>
    public string? AffidavitFrontUrl { get; set; }

    /// <summary>The affidavit, second page.</summary>
    public string? AffidavitBackUrl { get; set; }

    /// <summary>
    /// All of the above as one PDF on the documents account -- the
    /// legal_documents.pdf the owner asked for. Rebuilt whenever a photograph
    /// is added or replaced; null until there is at least one to bind.
    /// </summary>
    public string? LegalDocsPdfUrl { get; set; }

    /// <summary>Cloudinary's own id for that PDF, so a rebuild replaces the
    /// file instead of piling up copies of it.</summary>
    public string? LegalDocsPdfId { get; set; }
}
