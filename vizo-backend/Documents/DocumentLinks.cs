using System.Security.Cryptography;
using System.Text;

namespace vizo_backend.Documents;

/// <summary>
/// The signed, account-free link to a stored document, in one place.
///
/// ─────────────────────────── WHY IT IS SIGNED ──────────────────────────────
///
/// A Print button performs a plain browser navigation, which carries no
/// Authorization header — so it cannot point at an authenticated route. The
/// obvious answer is the Cloudinary URL, and one day it will be: PDF delivery
/// is blocked by default on accounts created since 2023, so a Cloudinary link
/// handed out today opens a 401. See <see cref="PdfStore"/>.
///
/// Until somebody ticks that box, the API serves the document itself at a URL
/// nobody can guess: an HMAC of the document's identity under the JWT signing
/// secret. Unguessable, stable for a given document, needs no column, and
/// rotating that secret revokes every link ever issued in one move.
///
/// ─────────────────────────── WHY IT IS HERE ────────────────────────────────
///
/// DocumentsController had this as two private methods, which was fine while it
/// was the only caller. It is not any more — SalesController hands out the link
/// to a sales return's credit note — and a signing scheme with two
/// implementations is a scheme that will be changed in one of them.
/// </summary>
public static class DocumentLinks
{
    /// <summary>
    /// The unguessable part of the link. Truncated to 22 characters: that is
    /// 132 bits of a SHA-256 HMAC, which is far past anything worth guessing,
    /// and it keeps the URL short enough to survive being pasted into WhatsApp.
    /// </summary>
    public static string Key(string? signingSecret, string kind, string key)
    {
        var secret = string.IsNullOrWhiteSpace(signingSecret) ? "advpos" : signingSecret!;
        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = mac.ComputeHash(Encoding.UTF8.GetBytes($"doc:{kind}:{key}"));
        return Convert.ToBase64String(hash)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=')[..22];
    }

    /// <summary>
    /// The full link, for the host answering this request. Derived rather than
    /// stored, so it is always right whether the API is on localhost, on
    /// staging or in production.
    /// </summary>
    public static string Share(string scheme, string host, string? signingSecret, string kind, string key) =>
        $"{scheme}://{host}/api/documents/open/{kind}/{Uri.EscapeDataString(key)}?k={Key(signingSecret, kind, key)}";

    /// <summary>
    /// Whether a key somebody presented is the right one, compared in constant
    /// time so the endpoint cannot be used as an oracle to work one out a
    /// character at a time.
    /// </summary>
    public static bool Matches(string? presented, string expected) =>
        !string.IsNullOrEmpty(presented) &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}
