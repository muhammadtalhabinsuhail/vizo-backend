using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

/* "Account" is a chart-of-accounts row in this project, so Cloudinary's own
   Account type needs a name of its own -- same alias UploadController uses. */
using CloudinaryAccount = CloudinaryDotNet.Account;

/* CloudinaryDotNet ships its own HttpMethod enum, which collides with the one
   in System.Net.Http the moment both namespaces are open. */
using HttpVerb = System.Net.Http.HttpMethod;

namespace vizo_backend.Documents;

/// <summary>
/// Puts a generated document on the documents Cloudinary account and hands
/// back the link.
///
/// UploadController already does this for a file a person picked; this is the
/// same thing for a file the API just made, and it reads the SAME
/// "CloudinaryPdfs" configuration section -- one account for every PDF the
/// system produces, so nothing is scattered across two clouds.
///
/// A PDF is a RAW asset. Sending it through ImageUploadParams makes Cloudinary
/// try to rasterise it, and the link then serves a picture of page one rather
/// than the document.
/// </summary>
public static class PdfStore
{
    /// <param name="Deliverable">
    /// Whether the URL actually SERVES the file to somebody with no account.
    ///
    /// This is checked rather than assumed because Cloudinary blocks PDF and
    /// ZIP delivery by default on accounts created since 2023 -- the upload
    /// succeeds, a perfectly ordinary-looking secure_url comes back, and every
    /// request to it answers 401 with "x-cld-error: deny or ACL failure". Both
    /// accounts this project is configured with are in that state today.
    ///
    /// The fix is one checkbox in the Cloudinary console:
    ///     Settings -> Security -> Restricted media types -> allow PDF.
    /// Until somebody ticks it, sending that link to a customer would send
    /// them a 401, so the caller falls back to a link this API serves itself.
    /// </param>
    public sealed record Stored(string Url, string PublicId, long Bytes, bool Deliverable);

    /* One client for the delivery check. HttpClient is expensive to make and
       cheap to share, and a socket per invoice is how you exhaust a port
       range. */
    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<Stored> UploadAsync(
        IConfiguration cfg, byte[] pdf, string fileName, string? subFolder = null)
    {
        var section = cfg.GetSection("CloudinaryPdfs");

        var client = new Cloudinary(new CloudinaryAccount(
            section["CloudName"], section["ApiKey"], section["ApiSecret"]));
        client.Api.Secure = true;

        var root = section["Folder"] ?? "advpos/documents";
        var folder = string.IsNullOrWhiteSpace(subFolder) ? root : $"{root}/{subFolder.Trim('/')}";

        await using var stream = new MemoryStream(pdf);

        var result = await client.UploadAsync(new RawUploadParams
        {
            File = new FileDescription(fileName, stream),
            Folder = folder,
            UseFilename = true,
            /* Unique names on purpose: re-issuing a bill must not silently
               replace the copy a customer was already sent. */
            UniqueFilename = true,
            Overwrite = false
        });

        if (result.Error is not null)
            throw new InvalidOperationException($"Cloudinary rejected the upload: {result.Error.Message}");

        var url = result.SecureUrl?.ToString();
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Cloudinary returned no URL for the upload.");

        return new Stored(url, result.PublicId, result.Bytes, await CanBeDelivered(url));
    }

    /// <summary>
    /// HEAD the URL we are about to hand out. A failure here is not an error --
    /// the asset is stored either way -- it only decides which link the app
    /// gives a customer.
    /// </summary>
    private static async Task<bool> CanBeDelivered(string url)
    {
        try
        {
            using var res = await Probe.SendAsync(new HttpRequestMessage(HttpVerb.Head, url));
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
