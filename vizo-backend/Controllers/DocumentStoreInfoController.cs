using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Documents;
using vizo_backend.Models;

namespace vizo_backend.Controllers;

/// <summary>
/// What the Document Store screen (/admin/documents) needs besides the list
/// itself: WHERE the files go, and WHICH kinds of document exist.
///
/// Both used to be typed into the page. The "Cloudinary folder" card printed
/// the literal "advpos/documents" whatever the configuration said (this very
/// worktree uploads to a test folder), and the kind filter was a hand-copied
/// list that had already fallen behind DocumentBuilder -- it had no sales
/// returns, no expense sheets and no purchase vouchers, so those documents
/// could be stored but never filtered for.
///
/// The folder is configuration (CloudinaryPdfs:Folder), not a secret: it is
/// part of every document link anyway. The kinds are DocumentBuilder.Kinds --
/// the documents the system can render -- together with every kind actually
/// present in "DocumentFile", which adds the archived reports and statements
/// ("report.sales-summary", "statement.balance-sheet") whose keys live in the
/// report and statement builders.
/// </summary>
[Route("api/documents")]
[ApiController]
[Authorize(Policy = "BackOffice")]
public class DocumentStoreInfoController : ApiControllerBase
{
    public DocumentStoreInfoController(AppDbContext db, IConfiguration cfg,
        ILogger<DocumentStoreInfoController> logger, IWebHostEnvironment env)
        : base(db, cfg, logger, env) { }

    /// <summary>
    /// A person's name for each kind DocumentBuilder renders. A kind with no
    /// entry here still appears, named from its key, so a new document type
    /// is never missing from the filter -- it is only less nicely named.
    /// </summary>
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["purchase-order"] = "Purchase orders",
        ["purchase-vouchers"] = "Purchase vouchers",
        ["purchase-invoice"] = "Purchase invoices",
        ["goods-receipt"] = "Goods receipts",
        ["purchase-return"] = "Purchase returns",
        ["sales-return"] = "Sales returns",
        ["stock-adjustment"] = "Stock adjustments",
        ["stock-transfer"] = "Stock transfers",
        ["voucher"] = "Vouchers",
        ["journal-entry"] = "Journal entries",
        ["expense"] = "Expense vouchers",
        ["party-statement"] = "Account statements",
        ["expense-sheet"] = "Expense sheets",
    };

    private static string LabelFor(string kind)
    {
        if (Labels.TryGetValue(kind, out var l)) return l;
        static string Words(string k) => k.Replace('-', ' ') is var w && w.Length > 0
            ? char.ToUpperInvariant(w[0]) + w[1..] : w;
        if (kind.StartsWith("report.", StringComparison.OrdinalIgnoreCase)) return $"Report · {Words(kind[7..])}";
        if (kind.StartsWith("statement.", StringComparison.OrdinalIgnoreCase)) return $"Statement · {Words(kind[10..])}";
        return Words(kind);
    }

    [HttpGet("store-info")]
    public async Task<IActionResult> StoreInfo()
    {
        try
        {
            var stored = await _db.DocumentFiles.AsNoTracking()
                .Select(f => f.DocKind).Distinct().ToListAsync();

            var kinds = DocumentBuilder.Kinds.Keys
                .Concat(stored)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(k => new { value = k, label = LabelFor(k) })
                /* Documents first, then reports, then statements -- the order
                   the old hand-typed list used. */
                .OrderBy(k => k.value.StartsWith("statement.") ? 2 : k.value.StartsWith("report.") ? 1 : 0)
                .ThenBy(k => k.label)
                .ToList();

            return Ok(new
            {
                folder = _cfg.GetSection("CloudinaryPdfs")["Folder"] ?? "advpos/documents",
                kinds
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "load the document store settings");
        }
    }
}
