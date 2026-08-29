using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Documents;

/// <summary>
/// The single path every generated PDF takes: render, push to the
/// "CloudinaryPdfs" account, record where it went.
///
/// NOTHING IN THIS PROJECT WRITES A PDF TO DISK. The bytes go from the
/// renderer straight to Cloudinary and, when a browser asked for them, straight
/// down the response. The API host's filesystem is not backed up and the host
/// can be replaced; a document that only exists next to the binary is one
/// deploy away from gone.
///
/// Re-archiving a document REPLACES its row rather than adding another, so
/// "DocumentFile" stays the size of the document set instead of growing with
/// every click. The old Cloudinary asset is left alone -- deleting it would
/// break a link somebody may already have been sent.
/// </summary>
public static class DocumentArchive
{
    /// <param name="Deliverable">
    /// False when Cloudinary accepted the upload but will not serve the file.
    /// See <see cref="PdfStore"/>: PDF delivery is off by default on accounts
    /// created since 2023, and the fix is a console setting, not code.
    /// </param>
    public sealed record Result(
        int FileId, string DocKind, string DocKey, string? DocNo,
        string FileName, string PdfUrl, string PublicId, long Bytes,
        bool Deliverable, DateTime GeneratedAt);

    /// <summary>
    /// Renders, uploads and records in one go.
    ///
    /// <paramref name="docKey"/> is the document's own id for a document, or a
    /// fingerprint of the parameters for a report -- a sales summary for August
    /// has no row anywhere to key off, and the same report re-run tomorrow must
    /// replace its file rather than pile up copies.
    /// </summary>
    public static async Task<Result> StoreAsync(
        AppDbContext db, IConfiguration cfg,
        string docKind, string docKey, string? docNo, string fileName,
        byte[] pdf, int userId, string subFolder)
    {
        var stored = await PdfStore.UploadAsync(cfg, pdf, fileName, subFolder);
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        var row = await db.DocumentFiles
            .FirstOrDefaultAsync(f => f.DocKind == docKind && f.DocKey == docKey);

        if (row is null)
        {
            row = new DocumentFile { DocKind = docKind, DocKey = docKey };
            db.DocumentFiles.Add(row);
        }

        row.DocNo = docNo;
        row.FileName = fileName;
        row.PdfUrl = stored.Url;
        row.PdfPublicId = stored.PublicId;
        row.Bytes = stored.Bytes;
        row.IsDeliverable = stored.Deliverable;
        row.GeneratedByUserId = userId;
        row.GeneratedAt = now;

        await db.SaveChangesAsync();

        return new Result(row.FileId, docKind, docKey, docNo, fileName,
            stored.Url, stored.PublicId, stored.Bytes, stored.Deliverable, now);
    }

    /// <summary>What is already archived for this document, or null.</summary>
    public static Task<DocumentFile?> FindAsync(AppDbContext db, string docKind, string docKey) =>
        db.DocumentFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.DocKind == docKind && f.DocKey == docKey);
}
