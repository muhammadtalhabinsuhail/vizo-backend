namespace vizo_backend.Services;

/// <summary>
/// Where the WEB APP lives, and how to turn an in-app path into a link a
/// person can actually tap.
///
/// ─────────────────────────── WHY THIS EXISTS ───────────────────────────────
///
/// Every notification in this system passes a path: "/sales/orders/42". That is
/// fine inside the app, where the browser fills in the origin. It is useless
/// everywhere else, and a notification is mostly read everywhere else:
///
///   · WEB PUSH. The payload goes to the browser's push service and is opened
///     by public/sw.js, which calls clients.openWindow(url). A bare path there
///     resolves against the SERVICE WORKER's scope -- which happens to work
///     while the app is the only thing on that origin, and stops working the
///     moment it is not. An absolute URL is unambiguous.
///
///   · ANYWHERE THE TEXT IS FORWARDED. Somebody screenshots the bell, or
///     pastes a notification into WhatsApp. "/sales/orders/42" is not a link.
///
/// So the origin is written down once, here, and read from configuration so it
/// can be changed without a deploy:
///
///     appsettings.json        "App": { "WebBaseUrl": "https://..." }
///     environment variable     App__WebBaseUrl=https://...
///
/// The environment variable WINS -- that is ASP.NET's own configuration
/// layering, not anything added here -- so staging and production each point
/// at themselves without either one editing the committed file.
///
/// ─────────────────────── WHAT THE BROWSER DOES WITH IT ─────────────────────
///
/// The bell turns an absolute URL whose origin matches the page it is on back
/// into a path before rendering it, so clicking a notification while you are
/// already in the app is still a client-side navigation and not a full page
/// load. See vizo-erp/src/lib/app-url.ts. A URL pointing somewhere else is left
/// alone and opens as an ordinary link, which is what you want when somebody is
/// reading production's bell on a staging build.
/// </summary>
public static class AppLinks
{
    /// <summary>
    /// The live site. Used when nothing is configured, so a deployment that
    /// forgets to set anything still sends people somewhere real rather than
    /// to localhost.
    /// </summary>
    public const string DefaultWebBaseUrl = "https://advpos-frontend.vercel.app";

    /// <summary>
    /// The configured origin, without a trailing slash.
    /// </summary>
    public static string WebBaseUrl(IConfiguration cfg)
    {
        var configured = cfg["App:WebBaseUrl"];
        var url = string.IsNullOrWhiteSpace(configured) ? DefaultWebBaseUrl : configured.Trim();
        return url.TrimEnd('/');
    }

    /// <summary>
    /// An in-app path as a full URL.
    ///
    /// Anything already absolute is returned untouched -- some call sites pass
    /// a Cloudinary link or the signed bill URL, and rewriting one of those
    /// would break it. A null or empty path stays null: the bell reads that as
    /// "nothing to open" rather than as a link to the home page.
    /// </summary>
    public static string? Absolute(IConfiguration cfg, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var p = path.Trim();
        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return p;

        if (!p.StartsWith('/')) p = "/" + p;
        return WebBaseUrl(cfg) + p;
    }
}
