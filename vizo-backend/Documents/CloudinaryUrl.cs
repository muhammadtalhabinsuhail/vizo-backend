namespace vizo_backend.Documents;

/// <summary>
/// Small helpers for the delivery URLs Cloudinary hands back.
///
/// NOT called "Cloudinary": that is the SDK's own client type, and this file
/// shares a namespace with PdfStore, which uses it. A class named Cloudinary
/// here silently shadows the SDK's -- the same trap as Claim and Account
/// colliding with the framework elsewhere in this codebase.
/// </summary>
public static class CloudinaryUrl
{
    /// <summary>
    /// Turns a stored delivery URL into one that downloads rather than opens
    /// in the browser's viewer, by inserting Cloudinary's `fl_attachment` flag
    /// after the delivery type.
    ///
    ///   .../raw/upload/v123/advpos/documents/PO-26-0042.pdf
    ///   .../raw/upload/fl_attachment/v123/advpos/documents/PO-26-0042.pdf
    ///
    /// Returns the URL untouched when <paramref name="attachment"/> is false,
    /// when it is already flagged, or when it does not look like a Cloudinary
    /// delivery URL at all -- a link that opens is always better than a link
    /// that 404s because a string was rewritten hopefully.
    /// </summary>
    public static string AsAttachment(string url, bool attachment = true)
    {
        if (!attachment || string.IsNullOrWhiteSpace(url)) return url;
        if (url.Contains("/fl_attachment", StringComparison.Ordinal)) return url;

        foreach (var marker in new[] { "/raw/upload/", "/image/upload/" })
        {
            var at = url.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var cut = at + marker.Length;
            return url[..cut] + "fl_attachment/" + url[cut..];
        }

        return url;
    }
}
