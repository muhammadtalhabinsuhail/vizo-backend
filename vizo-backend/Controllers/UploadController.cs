using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/* "Account" is a chart-of-accounts row in this project, so Cloudinary's own
   Account type needs a name of its own. */
using CloudinaryAccount = CloudinaryDotNet.Account;

namespace vizo_backend.Controllers;

/// <summary>
/// File upload. Two separate Cloudinary accounts are configured on purpose:
/// images go to one, PDFs to the other, and neither endpoint will accept the
/// other's file type.
///
/// The extension is never trusted on its own -- every upload is checked
/// against its magic bytes, because "invoice.pdf" renaming an .exe is the
/// oldest trick there is.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize(Policy = "Staff")]
public class UploadController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<UploadController> _logger;
    private readonly IWebHostEnvironment _env;

    public UploadController(IConfiguration cfg,
        ILogger<UploadController> logger, IWebHostEnvironment env)
    {
        _cfg = cfg;
        _logger = logger;
        _env = env;
    }

    private const long MaxImageBytes = 5 * 1024 * 1024;   //  5 MB
    private const long MaxPdfBytes = 15 * 1024 * 1024;    // 15 MB

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    private Cloudinary BuildClient(string section)
    {
        var s = _cfg.GetSection(section);
        var client = new Cloudinary(new CloudinaryAccount(s["CloudName"], s["ApiKey"], s["ApiSecret"]));
        client.Api.Secure = true;
        return client;
    }

    /* ─────────────────────────── IMAGES ─────────────────────────── */

    /// <summary>Uploads one image to the images Cloudinary account.</summary>
    [HttpPost("image")]
    [RequestSizeLimit(MaxImageBytes + 1024)]
    public async Task<IActionResult> UploadImage(IFormFile? file, [FromQuery] string? folder)
    {
        try
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Choose a file to upload." });

            if (file.Length > MaxImageBytes)
                return BadRequest(new { message = "Images must be 5 MB or smaller." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!ImageExtensions.Contains(ext))
                return BadRequest(new { message = "Only JPG, PNG, WEBP and GIF images are accepted." });

            await using var stream = file.OpenReadStream();
            if (!await LooksLikeImage(stream))
                return BadRequest(new { message = "That file is not a real image." });
            stream.Position = 0;

            var section = _cfg.GetSection("CloudinaryImages");
            var target = string.IsNullOrWhiteSpace(folder)
                ? section["Folder"] ?? "advpos/images"
                : $"{section["Folder"]}/{folder.Trim('/')}";

            var result = await BuildClient("CloudinaryImages").UploadAsync(new ImageUploadParams
            {
                File = new FileDescription(file.FileName, stream),
                Folder = target,
                UseFilename = true,
                UniqueFilename = true,
                Overwrite = false
            });

            if (result.Error is not null)
                return StatusCode(502, new { message = $"Cloudinary rejected the upload: {result.Error.Message}" });

            return Ok(new
            {
                url = result.SecureUrl?.ToString(),
                publicId = result.PublicId,
                width = result.Width,
                height = result.Height,
                format = result.Format,
                bytes = result.Bytes
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/upload/image");
        }
    }

    /* ──────────────────────────── PDFs ──────────────────────────── */

    /// <summary>Uploads one PDF to the documents Cloudinary account.</summary>
    [HttpPost("pdf")]
    [RequestSizeLimit(MaxPdfBytes + 1024)]
    public async Task<IActionResult> UploadPdf(IFormFile? file, [FromQuery] string? folder)
    {
        try
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { message = "Choose a file to upload." });

            if (file.Length > MaxPdfBytes)
                return BadRequest(new { message = "PDFs must be 15 MB or smaller." });

            if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "Only PDF files are accepted here." });

            await using var stream = file.OpenReadStream();
            if (!await LooksLikePdf(stream))
                return BadRequest(new { message = "That file is not a real PDF." });
            stream.Position = 0;

            var section = _cfg.GetSection("CloudinaryPdfs");
            var target = string.IsNullOrWhiteSpace(folder)
                ? section["Folder"] ?? "advpos/documents"
                : $"{section["Folder"]}/{folder.Trim('/')}";

            /* A PDF is a raw asset, not an image, so it must not go through
               ImageUploadParams -- Cloudinary would try to rasterise it. */
            var result = await BuildClient("CloudinaryPdfs").UploadAsync(new RawUploadParams
            {
                File = new FileDescription(file.FileName, stream),
                Folder = target,
                UseFilename = true,
                UniqueFilename = true,
                Overwrite = false
            });

            if (result.Error is not null)
                return StatusCode(502, new { message = $"Cloudinary rejected the upload: {result.Error.Message}" });

            return Ok(new
            {
                url = result.SecureUrl?.ToString(),
                publicId = result.PublicId,
                bytes = result.Bytes,
                originalName = file.FileName
            });
        }
        catch (Exception ex)
        {
            return Fail(ex, "save C:/Program Files/Git/api/upload/pdf");
        }
    }

    /* ─────────────────────────── DELETE ─────────────────────────── */

    [HttpDelete("image")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> DeleteImage([FromQuery] string publicId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicId)) return BadRequest(new { message = "publicId is required." });
            var r = await BuildClient("CloudinaryImages").DestroyAsync(new DeletionParams(publicId));
            return Ok(new { result = r.Result });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete C:/Program Files/Git/api/upload/image");
        }
    }

    [HttpDelete("pdf")]
    [Authorize(Policy = "SuperAdmin")]
    public async Task<IActionResult> DeletePdf([FromQuery] string publicId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicId)) return BadRequest(new { message = "publicId is required." });
            var r = await BuildClient("CloudinaryPdfs").DestroyAsync(new DeletionParams(publicId)
            {
                ResourceType = ResourceType.Raw
            });
            return Ok(new { result = r.Result });
        }
        catch (Exception ex)
        {
            return Fail(ex, "delete C:/Program Files/Git/api/upload/pdf");
        }
    }

    /* ───────────────────── magic-byte checks ────────────────────── */

    private static async Task<bool> LooksLikeImage(Stream s)
    {
        var head = new byte[12];
        var read = await s.ReadAsync(head.AsMemory(0, 12));
        if (read < 4) return false;

        // JPEG  FF D8 FF
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;
        // PNG   89 50 4E 47 0D 0A 1A 0A
        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return true;
        // GIF   "GIF8"
        if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x38) return true;
        // WEBP  "RIFF" .... "WEBP"
        if (read >= 12 && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 &&
            head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return true;

        return false;
    }

    private static async Task<bool> LooksLikePdf(Stream s)
    {
        var head = new byte[5];
        var read = await s.ReadAsync(head.AsMemory(0, 5));
        // "%PDF-"
        return read == 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D;
    }

    /// <summary>
    /// The single failure path for this controller.
    ///
    /// Logs the whole exception server-side, then answers with JSON the screen
    /// can show: what was being attempted, and the real message off the BASE
    /// exception -- Npgsql puts the useful text there (a constraint name, a
    /// null violation) while the outer DbUpdateException only ever says
    /// "An error occurred while saving the entity changes".
    ///
    /// The stack trace is attached in Development only.
    /// </summary>
    private IActionResult Fail(Exception ex, string what)
    {
        _logger.LogError(ex, "Failed to {What} ({Method} {Path})",
            what, Request.Method, Request.Path);

        return StatusCode(500, new
        {
            message = $"Could not {what}.",
            error = ex.GetBaseException().Message,
            type = ex.GetBaseException().GetType().Name,
            detail = _env.IsDevelopment() ? ex.ToString() : null
        });
    }

}
