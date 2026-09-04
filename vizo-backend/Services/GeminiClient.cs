using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace vizo_backend.Services;

/// <summary>
/// The one place this application talks to an AI model.
///
/// ─────────────────────────── THE RULE THAT GOVERNS ALL OF IT ───────────────
///
/// THE MODEL NEVER CALCULATES ANYTHING.
///
/// Every number -- how far sales fell, which customer stopped buying, what ran
/// out of stock -- is computed by SQL first and handed to the model as finished
/// JSON. The model's only job is to read those numbers and say, in a sentence a
/// shopkeeper would use, what they mean and what to do about it.
///
/// Ask a model "why did sales drop" without the numbers and it will invent an
/// answer, and the answer will sound completely convincing. Somebody will then
/// act on it. Numbers first, always.
///
/// ─────────────────────────── AND THREE MORE ────────────────────────────────
///
/// 1. The key never leaves the server. It is read from configuration (user
///    secrets locally, environment variables in production) and every call
///    originates here. Nothing AI-shaped is ever proxied to the browser with a
///    key attached.
///
/// 2. Everything this returns is a guess. Callers must label it on screen as
///    AI-written, never post an AI number to the ledger, and never let it
///    trigger an action by itself.
///
/// 3. If the model is down, the app is not. Every method returns null instead
///    of throwing, the failure is logged, and the caller shows its numbers
///    without the commentary -- the same fail-open rule DocumentArchive has
///    followed since the Cloudinary work. A sale must never fail because
///    Google had a bad afternoon.
/// </summary>
public class GeminiClient
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GeminiClient> _logger;

    public GeminiClient(IHttpClientFactory http, IConfiguration cfg, ILogger<GeminiClient> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;
    }

    private string? ApiKey => _cfg["Gemini:ApiKey"];
    private string Model => _cfg["Gemini:Model"] ?? "gemini-2.0-flash";
    private int TimeoutSeconds => int.TryParse(_cfg["Gemini:TimeoutSeconds"], out var t) ? t : 30;

    /// <summary>
    /// Whether an AI call can even be attempted. Screens call this to decide
    /// whether to offer the button at all -- a button that always fails is
    /// worse than no button.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.Equals(_cfg["Gemini:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ask the model to read <paramref name="factsJson"/> and answer.
    ///
    /// <paramref name="instruction"/> says what kind of answer is wanted;
    /// <paramref name="factsJson"/> is the SQL-computed truth it must work
    /// from. Returns null on any failure -- see rule 3 above.
    /// </summary>
    public async Task<string?> ExplainAsync(
        string instruction,
        string factsJson,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Gemini is not configured; skipping the call.");
            return null;
        }

        /* The system instruction is deliberately blunt and repeated in the
           user turn. Models drift towards being helpful, and "helpful" here
           means inventing a figure that was not in the data. */
        var prompt =
            instruction.Trim() + "\n\n" +
            "RULES YOU MUST FOLLOW:\n" +
            "- Use ONLY the numbers in the DATA below. Never calculate a new one, never estimate, never guess.\n" +
            "- If the data does not answer something, say so plainly instead of filling the gap.\n" +
            "- Write the way a Pakistani shopkeeper speaks: short sentences, Roman Urdu mixed with English where that is natural.\n" +
            "- No preamble, no sign-off, no markdown headings. Just the answer.\n\n" +
            "DATA:\n" + factsJson;

        var body = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                temperature = 0.2,      // low: this is analysis, not creative writing
                maxOutputTokens = 1024,
                topP = 0.9
            }
        };

        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

            /* The key goes in a header, not the query string -- a URL ends up
               in proxy logs and browser history in a way a header does not. */
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("x-goog-api-key", ApiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await client.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini returned {Status}: {Body}", (int)res.StatusCode, Trim(raw));
                return null;
            }

            return ReadFirstText(raw);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Gemini timed out after {Seconds}s.", TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            /* Logged and swallowed on purpose. The caller has real numbers to
               show; losing the commentary is a much smaller loss than losing
               the screen. */
            _logger.LogWarning(ex, "Gemini call failed.");
            return null;
        }
    }

    /// <summary>
    /// Pull the answer text out of the response envelope.
    /// Returns null rather than throwing on any shape it does not recognise --
    /// a model that changes its response shape must not take a screen down.
    /// </summary>
    private string? ReadFirstText(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.GetArrayLength() == 0)
            {
                _logger.LogWarning("Gemini returned no candidates: {Body}", Trim(raw));
                return null;
            }

            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }

            var answer = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(answer) ? null : answer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Gemini response.");
            return null;
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
