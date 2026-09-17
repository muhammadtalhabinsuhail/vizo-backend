namespace vizo_backend.Models;

/// <summary>
/// HAND-WRITTEN PARTIAL -- not produced by scaffolding.
///
/// SalesInvoice.cs is scaffolded from Neon and is overwritten by the next
/// dotnet ef dbcontext scaffold, so the column this project added lives here.
/// Mapped in AppDbContext.Custom.cs; created on Neon by
/// backend/database/18_sales_scope_returns_and_places.sql.
///
/// AFTER A RE-SCAFFOLD: delete this file, or you will get a duplicate
/// definition -- the column exists on Neon now and a fresh scaffold generates
/// it inside SalesInvoice.cs itself.
/// </summary>
public partial class SalesInvoice
{
    /// <summary>
    /// Whether the Cloudinary copy of this bill can actually be OPENED by
    /// somebody holding the link.
    ///
    /// <see cref="PdfUrl"/> being set means the upload succeeded. It does NOT
    /// mean the file can be fetched: Cloudinary blocks PDF delivery by default
    /// on accounts created since 2023, so the upload returns a perfectly
    /// ordinary secure_url and every request to it answers 401.
    ///
    /// That is why "Print bill" was opening an error page for every role. The
    /// check has always been made -- Documents/PdfStore.cs HEADs the URL after
    /// uploading -- but for a sale invoice the answer had nowhere to live, so
    /// the screen went on offering a link it had been told was dead.
    ///
    /// False (the default, and the truth for every row that predates this
    /// column) makes the API hand out its own signed /api/sales/bill/... link,
    /// which renders the same bytes and needs no account.
    ///
    /// Turn PDF delivery on in the Cloudinary console and this starts coming
    /// back true on the next rebuild, with no code change.
    /// </summary>
    public bool PdfDeliverable { get; set; }
}
