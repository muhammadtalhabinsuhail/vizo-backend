namespace vizo_backend.Models;

/// <summary>
/// One generated PDF and the Cloudinary link it was pushed to.
///
/// HAND-WRITTEN, not scaffolded. Created on Neon by
/// backend/database/10_document_files.sql; its EF configuration lives in
/// AppDbContext.Custom.cs so a re-scaffold cannot lose it.
///
/// NOTE FOR A FUTURE RE-SCAFFOLD: "DocumentFile" now exists on Neon, so a fresh
/// scaffold WILL generate its own DocumentFile.cs and DbSet. Delete this file
/// and the block in AppDbContext.Custom.cs first, or you get duplicate
/// definitions -- same trap as PasswordResetCode.
/// </summary>
public partial class DocumentFile
{
    public int FileId { get; set; }

    /// <summary>purchase.order, stock.transfer, report.aging.customer, and so on.</summary>
    public string DocKind { get; set; } = null!;

    /// <summary>
    /// The document's own id, or -- for a report, which has no row anywhere --
    /// a fingerprint of the parameters it was run with.
    /// </summary>
    public string DocKey { get; set; } = null!;

    /// <summary>The human-readable number, when the document has one.</summary>
    public string? DocNo { get; set; }

    public string FileName { get; set; } = null!;

    public string PdfUrl { get; set; } = null!;

    public string PdfPublicId { get; set; } = null!;

    public long Bytes { get; set; }

    /// <summary>
    /// Whether the store will actually SERVE the file to somebody with no
    /// account. False means the upload worked but the link answers 401 --
    /// see PdfStore for why that happens and what fixes it.
    /// </summary>
    public bool IsDeliverable { get; set; }

    public int GeneratedByUserId { get; set; }

    public DateTime GeneratedAt { get; set; }
}
