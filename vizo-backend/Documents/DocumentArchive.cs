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
        var now = vizo_backend.Services.BusinessClock.Now();

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

    /// <summary>
    /// Builds, uploads and records a document by kind and id, swallowing any
    /// failure.
    ///
    /// This is what every CREATE action calls. A document is archived the moment
    /// it exists, not the first time somebody presses a button, so Download and
    /// Print always have a stored Cloudinary file to hand out.
    ///
    /// The failure is swallowed ON PURPOSE. By the time this runs the order has
    /// been taken, the stock has moved and the money is in the drawer; failing
    /// the request because a document store was briefly unreachable would tell
    /// the operator the sale did not happen, and they would ring it up twice.
    /// The PDF can be rebuilt from the row at any time -- the sale cannot.
    /// </summary>
    public static async Task<Result?> TryStoreForAsync(
        AppDbContext db, IConfiguration cfg, ILogger logger,
        string kind, int id, int userId)
    {
        try
        {
            if (!DocumentBuilder.Kinds.TryGetValue(kind, out var folder))
            {
                logger.LogWarning("No document kind '{Kind}' to archive", kind);
                return null;
            }

            var doc = await DocumentBuilder.BuildAsync(db, kind, id);
            if (doc is null)
            {
                logger.LogWarning("Nothing to archive for {Kind} {Id}", kind, id);
                return null;
            }

            return await StoreAsync(db, cfg, kind, id.ToString(), doc.DocNo,
                DocumentBuilder.FileName(kind, doc.DocNo, id),
                DocumentPdf.Render(doc), userId, folder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{Kind} {Id} was saved but its PDF could not be archived. It can be rebuilt from the row.",
                kind, id);
            return null;
        }
    }

    /// <summary>
    /// The stored file for a document, archiving it first if it has never been
    /// archived. What the Download and Print routes call, so the link they hand
    /// out is always the Cloudinary one.
    /// </summary>
    public static async Task<DocumentFile?> EnsureAsync(
        AppDbContext db, IConfiguration cfg, ILogger logger,
        string kind, int id, int userId)
    {
        var existing = await FindAsync(db, kind, id.ToString());
        if (existing is not null) return existing;

        var stored = await TryStoreForAsync(db, cfg, logger, kind, id, userId);
        return stored is null ? null : await FindAsync(db, kind, id.ToString());
    }

    /// <summary>What is already archived for this document, or null.</summary>
    public static Task<DocumentFile?> FindAsync(AppDbContext db, string docKind, string docKey) =>
        db.DocumentFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.DocKind == docKind && f.DocKey == docKey);
}
