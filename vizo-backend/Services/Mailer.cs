using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace vizo_backend.Services;

/// <summary>
/// Sends the few emails this system sends: a password reset code, and the
/// temporary password a new (or reset) account starts with.
///
/// ─────────────────────────── WHY A STATIC CLASS ────────────────────────────
///
/// AuthController already had a private SendResetEmail, and the forgot-password
/// action that called it was commented out. AuthController is one of Talha's
/// five files, so it is not edited; the new recovery endpoint, the admin's
/// "send reset code" and the new-user invite all need the same thing, so it
/// lives here once. Static on purpose -- the project brief is no services in
/// DI -- and everything it needs (the EmailSettings section) is handed in.
///
/// ─────────────────────────── CONFIGURATION ─────────────────────────────────
///
///     "EmailSettings": {
///       "SmtpHost": "smtp.gmail.com", "SmtpPort": 587,
///       "SenderEmail": "...", "SenderPassword": "...", "SenderName": "AdvPOS",
///       "PickupDirectory": ""          &lt;-- optional, see below
///     }
///
/// PICKUP DIRECTORY (testing). When EmailSettings:PickupDirectory is set, the
/// message is NOT sent: it is written to that folder as a .eml file instead
/// and nothing leaves the machine. That is how the flows were tested without
/// mailing real staff addresses -- set it in appsettings.Development.json only
/// (that file is gitignored), open the .eml, read the code. Production leaves
/// it empty and the real SMTP server is used. It is the same trick .NET's old
/// SmtpClient called SpecifiedPickupDirectory.
///
/// ─────────────────────────── FAILURE ───────────────────────────────────────
///
/// SendAsync THROWS on failure. Each caller decides what the person on the
/// other end may learn: the anonymous forgot-password endpoint swallows it
/// (a different reply would tell a stranger the address is real), while the
/// Super Admin is told the email did not go -- and is shown the temporary
/// password on screen regardless, so nobody is ever left without a way in.
/// </summary>
public static class Mailer
{
    /// <summary>
    /// A connection that hangs is worse than one that fails: the admin's
    /// "Create user" button would spin for the framework's default 100 s. Fifteen
    /// seconds is ample for any SMTP server that is actually up.
    /// </summary>
    private const int TimeoutMs = 15_000;

    /// <summary>Sends one message, or writes it to the pickup folder. Throws on failure.</summary>
    public static async Task SendAsync(IConfiguration cfg, string toEmail, string toName,
                                       string subject, string html, string text)
    {
        var s = cfg.GetSection("EmailSettings");
        var host = s["SmtpHost"] ?? "";
        var sender = s["SenderEmail"] ?? "";
        var secret = s["SenderPassword"] ?? "";

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s["SenderName"] ?? "AdvPOS", sender));
        msg.To.Add(new MailboxAddress(toName, toEmail));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

        var pickup = s["PickupDirectory"];
        if (!string.IsNullOrWhiteSpace(pickup))
        {
            Directory.CreateDirectory(pickup);
            var safe = string.Concat(toEmail.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var path = Path.Combine(pickup, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{safe}.eml");
            await msg.WriteToAsync(path);
            return;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(sender))
            throw new InvalidOperationException("EmailSettings:SmtpHost / SenderEmail are not configured.");

        /* 465 is TLS from the first byte; anything else (587) must upgrade with
           STARTTLS. Not "Auto": that falls back to plain text when a server
           does not offer STARTTLS, and the password would then cross in clear. */
        var port = s.GetValue("SmtpPort", 587);
        using var client = new SmtpClient { Timeout = TimeoutMs };
        await client.ConnectAsync(host, port,
            port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        if (secret.Length > 0)
            await client.AuthenticateAsync(sender, secret);
        await client.SendAsync(msg);
        await client.DisconnectAsync(true);
    }

    /// <summary>The six-digit reset code -- same wording and look as AuthController's original.</summary>
    public static Task SendResetCodeAsync(IConfiguration cfg, string to, string name, string code, int minutes,
                                          bool askedByAdmin = false)
    {
        var why = askedByAdmin
            ? "Your administrator has asked for a password reset on your account."
            : "Somebody (hopefully you) asked to reset the password on your account.";

        var html = Frame($@"
              <h2 style=""margin:0 0 8px;font-size:20px"">Reset your AdvPOS password</h2>
              <p style=""margin:0 0 8px;color:#475569"">Hello {Enc(name)}. {why}</p>
              <p style=""margin:0 0 24px;color:#475569"">Enter this code on the <b>Forgot password</b> screen, then choose a new password.</p>
              {CodeBox(code, 10)}
              <p style=""margin:24px 0 0;color:#475569"">It is valid for {minutes} minutes and can be used once.</p>
              <p style=""margin:12px 0 0;color:#94a3b8;font-size:13px"">
                Your current password still works until you choose a new one. Did not expect this? You can ignore it.
              </p>{LinkLine(cfg, "/forgot-password?email=" + Uri.EscapeDataString(to), "Open the reset screen")}");

        var text = $"Your AdvPOS password reset code is {code}. It is valid for {minutes} minutes. " +
                   "Your current password keeps working until you choose a new one.";

        return SendAsync(cfg, to, name, $"{code} is your AdvPOS password reset code", html, text);
    }

    /// <summary>
    /// A temporary password: for a brand-new account (the invite) or after the
    /// administrator reset one. The person must change it on first sign-in.
    /// </summary>
    public static Task SendTemporaryPasswordAsync(IConfiguration cfg, string to, string name,
                                                  string password, bool isNewAccount)
    {
        var heading = isNewAccount ? "Your AdvPOS account is ready" : "Your AdvPOS password was reset";
        var lead = isNewAccount
            ? "An account has been created for you. Sign in with this address and the temporary password below."
            : "Your administrator has set a temporary password on your account. Sign in with it below.";

        var html = Frame($@"
              <h2 style=""margin:0 0 8px;font-size:20px"">{heading}</h2>
              <p style=""margin:0 0 8px;color:#475569"">Hello {Enc(name)}. {lead}</p>
              <p style=""margin:0 0 20px;color:#475569"">Email: <b>{Enc(to)}</b></p>
              {CodeBox(password, 3)}
              <p style=""margin:24px 0 0;color:#475569"">You will be asked to choose your own password straight after signing in.</p>
              <p style=""margin:12px 0 0;color:#94a3b8;font-size:13px"">Do not forward this message.</p>{LinkLine(cfg, "/login", "Sign in to AdvPOS")}");

        var text = $"{heading}. Email: {to}. Temporary password: {password}. " +
                   "You will be asked to choose your own password after signing in.";

        return SendAsync(cfg, to, name, heading, html, text);
    }

    /* ─────────────────────────── helpers ─────────────────────────── */

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string Frame(string inner) =>
        $@"<div style=""font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:0 auto;padding:32px;color:#0f172a"">{inner}
            </div>";

    private static string CodeBox(string value, int spacing) =>
        $@"<div style=""background:#0f172a;color:#facc15;font-size:30px;font-weight:700;letter-spacing:{spacing}px;
                    text-align:center;padding:20px;border-radius:10px;font-family:Consolas,monospace"">{Enc(value)}</div>";

    /// <summary>A link to the web app, when App:WebBaseUrl is configured; nothing otherwise.</summary>
    private static string LinkLine(IConfiguration cfg, string path, string label)
    {
        var baseUrl = cfg["App:WebBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return "";
        var href = baseUrl.TrimEnd('/') + path;
        return $@"<p style=""margin:20px 0 0""><a href=""{Enc(href)}"" style=""color:#0f172a;font-weight:600"">{label} &rarr;</a></p>";
    }
}
