using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// Every printable document in the system except the sale invoice, which keeps
/// its own renderer and its own route because it is the one a customer sees.
///
/// Two verbs on the same URL, and the difference matters:
///
///   GET  /documents/{kind}/{id}/pdf   renders from the database and streams
///                                     the bytes. Print and Preview use this.
///   POST /documents/{kind}/{id}/pdf   renders, pushes to the "CloudinaryPdfs"
///                                     account and records the link in
///                                     "DocumentFile". Archive and Share use
///                                     this.
///
/// NOTHING IS WRITTEN TO DISK anywhere in this project. Before this controller
/// existed, none of these documents were PDFs at all -- every Print button on
/// the purchase, inventory and accounting screens called window.print(), which
/// prints the web page with its sidebar and its buttons, and stores nothing.
/// The report toolbar's "Export PDF" only produced a toast.
///
/// All of it now comes off the database and goes to the same Cloudinary
/// account as the bills.
/// </summary>
[Route("api/documents")]
[ApiController]
[Authorize(Policy = "Staff")]
public class DocumentsController : ApiControllerBase
{
    public DocumentsController(AppDbContext db, IConfiguration cfg,
        ILogger<DocumentsController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /* Document kinds, and the Cloudinary sub-folder each lands in. The keys are
       what the front end sends; keeping them in one place means a typo is a
       404 with a helpful message rather than a silent empty document. */
    /* The kinds and their Cloudinary folders live in DocumentBuilder, because
       the create actions in Purchases, Inventory and Accounting archive their
       own documents and need the same list. */
    private static IReadOnlyDictionary<string, string> Kinds => DocumentBuilder.Kinds;

    // ══════════════════════════════════════════════════════════════════
    //  RENDER  /  ARCHIVE
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The document as bytes, rebuilt from the row on every request.</summary>
    [HttpGet("{kind}/{id:int}/pdf")]
    public async Task<IActionResult> Render(string kind, int id)
    {
        try
        {
            if (!Kinds.ContainsKey(kind))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var doc = await DocumentBuilder.BuildAsync(_db, kind, id);
            if (doc is null) return NotFound(new { message = $"No {kind.Replace('-', ' ')} with id {id}." });

            var name = DocumentBuilder.FileName(kind, doc.DocNo, id);
            Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"render the {kind.Replace('-', ' ')}");
        }
    }

    /// <summary>
    /// Renders and pushes the document to the documents Cloudinary account.
    ///
    /// Re-callable. Pass force=true to rebuild one that is already archived --
    /// the company address changes, or the first upload lost the network, and
    /// the fix has to be one button.
    /// </summary>
    [HttpPost("{kind}/{id:int}/pdf")]
    public async Task<IActionResult> Archive(string kind, int id, [FromQuery] bool force = false)
    {
        try
        {
            if (!Kinds.TryGetValue(kind, out var folder))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var key = id.ToString();

            if (!force)
            {
                var existing = await DocumentArchive.FindAsync(_db, kind, key);
                if (existing is not null)
                    return Ok(Shape(existing, rebuilt: false, "That document was already archived."));
            }

            var doc = await DocumentBuilder.BuildAsync(_db, kind, id);
            if (doc is null) return NotFound(new { message = $"No {kind.Replace('-', ' ')} with id {id}." });

            var name = DocumentBuilder.FileName(kind, doc.DocNo, id);
            var result = await DocumentArchive.StoreAsync(_db, _cfg, kind, key, doc.DocNo, name,
                DocumentPdf.Render(doc), CurrentUserId(), folder);

            await Log("DOCUMENT_ARCHIVED", kind, doc.DocNo ?? key, result.PdfUrl, 1);
            return Ok(Shape(result, rebuilt: true, $"{doc.DocNo ?? doc.Title} saved to the document store."));
        }
        catch (Exception ex)
        {
            return Fail(ex, $"archive the {kind.Replace('-', ' ')}");
        }
    }

    /// <summary>
    /// Sends the caller to the document's own file in the Cloudinary store,
    /// archiving it first if it has never been archived.
    ///
    /// THIS IS WHAT PRINT AND DOWNLOAD USE. The alternative -- rendering the
    /// PDF fresh on every click -- worked, but it meant the file people looked
    /// at was never the file that had been stored, and the stored copy was only
    /// ever exercised when somebody pressed Share. Two paths to the same
    /// document is two things that can disagree. Now there is one: the bytes on
    /// screen ARE the bytes in the store.
    ///
    /// It falls back to rendering only when the store cannot be reached, so a
    /// Cloudinary outage degrades to "the PDF still opens" rather than to a
    /// broken button.
    /// </summary>
    [HttpGet("{kind}/{id:int}/download")]
    public async Task<IActionResult> Download(string kind, int id, [FromQuery] bool attachment = false)
    {
        try
        {
            if (!Kinds.ContainsKey(kind))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var file = await DocumentArchive.EnsureAsync(_db, _cfg, _logger, kind, id, CurrentUserId());

            if (file is not null && file.IsDeliverable)
                return Redirect(CloudinaryUrl.AsAttachment(file.PdfUrl, attachment));

            /* No stored copy, or the store will not serve it. Render and stream
               rather than leave the operator with nothing. */
            var doc = await DocumentBuilder.BuildAsync(_db, kind, id);
            if (doc is null) return NotFound(new { message = $"No {kind.Replace('-', ' ')} with id {id}." });

            var name = DocumentBuilder.FileName(kind, doc.DocNo, id);
            Response.Headers.ContentDisposition =
                $"{(attachment ? "attachment" : "inline")}; filename=\"{name}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, $"open the {kind.Replace('-', ' ')}");
        }
    }

    /// <summary>What is already archived for a document, if anything.</summary>
    [HttpGet("{kind}/{id:int}/file")]
    public async Task<IActionResult> StoredFile(string kind, int id)
    {
        try
        {
            if (!Kinds.ContainsKey(kind))
                return NotFound(new { message = $"'{kind}' is not a document this system prints." });

            var row = await DocumentArchive.FindAsync(_db, kind, id.ToString());
            return row is null
                ? Ok(new { archived = false })
                : Ok(Shape(row, rebuilt: false, null));
        }
        catch (Exception ex)
        {
            return Fail(ex, "look up the stored document");
        }
    }

    /// <summary>
    /// Everything the system has generated, newest first. This is the screen to
    /// open when somebody asks "where do the PDFs go" -- every row carries the
    /// Cloudinary link it was pushed to.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "BackOffice")]
    public async Task<IActionResult> List([FromQuery] string? kind, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize is < 1 or > 200) pageSize = 50;

            var rows = _db.DocumentFiles.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(kind)) rows = rows.Where(f => f.DocKind == kind);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                rows = rows.Where(f => (f.DocNo != null && f.DocNo.ToLower().Contains(term))
                                    || f.FileName.ToLower().Contains(term));
            }

            var total = await rows.CountAsync();
            var items = await rows
                .OrderByDescending(f => f.GeneratedAt)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(f => new
                {
                    id = f.FileId,
                    kind = f.DocKind,
                    docKey = f.DocKey,
                    docNo = f.DocNo,
                    fileName = f.FileName,
                    pdfUrl = f.PdfUrl,
                    bytes = f.Bytes,
                    isDeliverable = f.IsDeliverable,
                    generatedAt = f.GeneratedAt,
                    generatedBy = _db.Users.Where(u => u.UserId == f.GeneratedByUserId)
                        .Select(u => u.FullName).FirstOrDefault()
                })
                .ToListAsync();

            return Ok(new
            {
                total, page, pageSize,
                undeliverable = await _db.DocumentFiles.CountAsync(f => !f.IsDeliverable),
                items = items.Select(i => new
                {
                    i.id, i.kind, i.docKey, i.docNo, i.fileName, i.pdfUrl, i.bytes,
                    i.isDeliverable, i.generatedAt, i.generatedBy,
                    shareUrl = ShareLink(i.kind, i.docKey)
                })
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the document store");
        }
    }

    /// <summary>
    /// A stored document, to anybody holding the link. Anonymous on purpose:
    /// this is what goes out over WhatsApp, and the person on the other end has
    /// no account here. Same signing scheme as the sale-invoice bill link.
    /// </summary>
    [HttpGet("open/{kind}/{key}")]
    [AllowAnonymous]
    public async Task<IActionResult> PublicDocument(string kind, string key, [FromQuery] string? k)
    {
        try
        {
            var expected = DocumentKey(kind, key);
            if (string.IsNullOrEmpty(k) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(k), Encoding.UTF8.GetBytes(expected)))
                return NotFound(new { message = "That link is not valid." });

            if (!Kinds.ContainsKey(kind) || !int.TryParse(key, out var id))
                return NotFound(new { message = "That link is not valid." });

            var doc = await DocumentBuilder.BuildAsync(_db, kind, id);
            if (doc is null) return NotFound(new { message = "That document no longer exists." });

            Response.Headers.ContentDisposition = $"inline; filename=\"{DocumentBuilder.FileName(kind, doc.DocNo, id)}\"";
            return File(DocumentPdf.Render(doc), "application/pdf");
        }
        catch (Exception ex)
        {
            return Fail(ex, "open that document");
        }
    }


    /// <summary>
    /// Same signing scheme as the sale-invoice bill link: an HMAC of the
    /// document identity under the JWT signing secret. Unguessable, needs no
    /// column, and rotating that secret revokes every link at once.
    /// </summary>
    private string DocumentKey(string kind, string key)
    {
        var secret = _cfg["Jwt:Key"] ?? "advpos";
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = mac.ComputeHash(Encoding.UTF8.GetBytes($"doc:{kind}:{key}"));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..22];
    }

    private string ShareLink(string kind, string key) =>
        $"{Request.Scheme}://{Request.Host}/api/documents/open/{kind}/{Uri.EscapeDataString(key)}?k={DocumentKey(kind, key)}";

    private object Shape(DocumentFile f, bool rebuilt, string? message) => new
    {
        archived = true,
        fileId = f.FileId,
        kind = f.DocKind,
        docNo = f.DocNo,
        fileName = f.FileName,
        pdfUrl = f.PdfUrl,
        bytes = f.Bytes,
        isDeliverable = f.IsDeliverable,
        generatedAt = f.GeneratedAt,
        shareUrl = ShareLink(f.DocKind, f.DocKey),
        rebuilt,
        message
    };

    private object Shape(DocumentArchive.Result r, bool rebuilt, string? message) => new
    {
        archived = true,
        fileId = r.FileId,
        kind = r.DocKind,
        docNo = r.DocNo,
        fileName = r.FileName,
        pdfUrl = r.PdfUrl,
        bytes = r.Bytes,
        isDeliverable = r.Deliverable,
        generatedAt = r.GeneratedAt,
        shareUrl = ShareLink(r.DocKind, r.DocKey),
        rebuilt,
        message
    };
}
