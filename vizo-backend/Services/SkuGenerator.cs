using System.Text;
using System.Text.RegularExpressions;

namespace vizo_backend.Services;

/// <summary>
/// Builds a product's SKU from what the person typed as its name.
///
/// ─────────────────────────────── THE SHAPE ────────────────────────────────
///
///     VZ - WORD - MODEL - CAT - COLOR - NN
///
///     VZ      the brand, always. The company sells under one name.
///     WORD    the first three letters of the first meaningful word of the name
///     MODEL   the model number, when there is one (T9, V65, X2, VPD45W)
///     CAT     the first three letters of the category
///     COLOR   a three-letter colour code, when the name says a colour
///     NN      a serial, so the same thing typed twice is still two SKUs
///
///     "VIZO Titan T9 Wireless Earbuds - Black"  in  Earbuds
///                                    ->  VZ-TIT-T9-EAR-BLK-01
///
/// MODEL and COLOR are left out when the name has none, rather than replaced
/// with filler: "VZ-KUN-NEC-01" is a SKU, "VZ-KUN-XXX-NEC-XXX-01" is noise.
///
/// ─────────────────────────────── WHY IT IS HERE ───────────────────────────
///
/// On the server, not in the form. A SKU has to be unique, and the only place
/// that can promise that is the one that can see the table. The form asks for a
/// PREVIEW so the person sees it while typing; the real one is worked out again
/// at save time, because between typing and saving somebody else may have
/// taken the number.
///
/// ─────────────────────────────── THE JUDGEMENT ────────────────────────────
///
/// Nothing here is exact, because a product name is free text. The rules are
/// deliberately simple and in one place so they can be argued with:
///
///   · The brand word is skipped (it is "VZ" already).
///   · Generic descriptors -- WIRELESS, BLUETOOTH, and so on -- are never the
///     WORD. "Wireless Earbuds" is a description, not a name.
///   · Words that are the category itself are never the WORD either, or every
///     earbud would start VZ-EAR-.
///   · A token with letters AND digits, that is not a capacity ("10000MAH",
///     "20W"), is the MODEL. A capacity is a spec, not a model.
///   · A colour is the last word of "... - Black", or a colour word anywhere.
/// </summary>
public static class SkuGenerator
{
    public const string Brand = "VZ";

    /// <summary>What a barcode has to look like to be taken as a SKU.</summary>
    public static readonly Regex SkuPattern =
        new(@"^VZ(-[A-Z0-9]{1,8}){2,6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The bare parts, before the serial is added. Also the preview's explanation.</summary>
    public sealed record Parts(string Word, string? Model, string Category, string? Color)
    {
        /// <summary>Everything up to and including the trailing hyphen.</summary>
        public string Stem
        {
            get
            {
                var sb = new StringBuilder(Brand).Append('-').Append(Word);
                if (!string.IsNullOrEmpty(Model)) sb.Append('-').Append(Model);
                sb.Append('-').Append(Category);
                if (!string.IsNullOrEmpty(Color)) sb.Append('-').Append(Color);
                return sb.Append('-').ToString();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  WORD LISTS
    // ══════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BLACK"] = "BLK", ["WHITE"] = "WHT", ["RED"] = "RED", ["BLUE"] = "BLU",
        ["GREEN"] = "GRN", ["GREY"] = "GRY", ["GRAY"] = "GRY", ["PINK"] = "PNK",
        ["GOLD"] = "GLD", ["GOLDEN"] = "GLD", ["SILVER"] = "SLV", ["PURPLE"] = "PRP",
        ["ORANGE"] = "ORG", ["YELLOW"] = "YEL", ["BROWN"] = "BRN", ["NAVY"] = "NVY",
        ["BEIGE"] = "BGE", ["CYAN"] = "CYN", ["MAROON"] = "MRN", ["VIOLET"] = "VLT",
        ["TRANSPARENT"] = "CLR", ["CLEAR"] = "CLR", ["MULTI"] = "MLT", ["MULTICOLOR"] = "MLT",
        ["TITANIUM"] = "TTN", ["CHAMPAGNE"] = "CHP", ["COPPER"] = "CPR", ["BRONZE"] = "BRZ",
    };

    /* "Sky Blue", "Rose Gold", "Dark Green" -- a word that only qualifies the
       colour after it. Recognised so the colour is not read as "Sky". */
    private static readonly HashSet<string> ColorModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "DARK", "LIGHT", "SKY", "ROSE", "MIDNIGHT", "SPACE", "MATTE", "GLOSSY", "DEEP", "PALE", "ARMY",
        /* LED bulbs: "Cool White" and "Warm White" are two products, not one
           product and a word called COOL. */
        "COOL", "WARM",
    };

    /* Descriptions, not names. A product called "Wireless Earbuds T9" is a T9;
       "WIR" tells nobody anything. */
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "THE", "AND", "FOR", "WITH", "UNDER", "NEW", "OF", "IN", "BY", "A", "AN",
        "WIRELESS", "BLUETOOTH", "WIRED", "SMART", "MOBILE", "PHONE", "FAST", "QUICK",
        "PREMIUM", "ORIGINAL", "ORIGNAL", "ULTRA", "PLUS", "MINI", "MAX", "PACK", "SET",
        "USB", "TYPE", "TYPEC", "TYPE-C", "PCS", "PC", "PIECE",
    };

    /* "10000MAH", "20W", "65W", "1.5M", "128GB": a specification, not a model. */
    private static readonly Regex Capacity = new(
        @"^\d+(\.\d+)?(MAH|MA|W|V|A|MM|CM|M|GB|MB|TB|HZ|K|KG|G|ML|L|PCS|PC|IN|INCH)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HasDigit = new(@"\d", RegexOptions.Compiled);
    private static readonly Regex HasLetter = new(@"[A-Z]", RegexOptions.Compiled);
    private static readonly Regex Letters = new(@"[^A-Z]", RegexOptions.Compiled);

    // ══════════════════════════════════════════════════════════════════
    //  NAMES
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The name as compared for "is this already in the catalogue": trimmed,
    /// runs of spaces collapsed to one, case ignored.
    ///
    /// "VIZO  Titan T9 " and "vizo titan t9" are the same product to a person
    /// reading a shelf label, and a check that treats them as different is a
    /// check that lets the duplicate through.
    /// </summary>
    public static string NormalName(string? name) =>
        Regex.Replace((name ?? "").Trim(), @"\s+", " ").ToUpperInvariant();

    // ══════════════════════════════════════════════════════════════════
    //  PARSING
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Works out WORD, MODEL, CAT and COLOR from a name and its category.
    /// <paramref name="brandName"/> is whatever brand is selected, so a name
    /// that starts with it does not have that word taken as the WORD.
    /// </summary>
    public static Parts Parse(string? name, string? categoryName, string? brandName)
    {
        var raw = (name ?? "").ToUpperInvariant();

        /* Dashes that separate ("Earbuds - Black", "Earbuds — Black") become a
           marker; the hyphen inside "TYPE-C" or "T-9" is left alone. */
        raw = Regex.Replace(raw, @"\s+[-–—]+\s+|\s*[–—]\s*", " | ");
        raw = Regex.Replace(raw, @"[()\[\],/+&]", " ");

        var (color, rest) = TakeColor(raw);

        var categoryWords = WordsOf(categoryName);
        var brandWords = WordsOf(brandName);
        brandWords.Add("VIZO");

        var tokens = rest
            .Split(new[] { ' ', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('-', '.', '\'', '"'))
            .Where(t => t.Length > 0)
            .ToList();

        /* MODEL: first token that mixes letters and digits and is not a spec. */
        var model = tokens.FirstOrDefault(t =>
            HasDigit.IsMatch(t) && HasLetter.IsMatch(t) &&
            !Capacity.IsMatch(t) && t.Length is >= 2 and <= 8 &&
            !brandWords.Contains(t));

        /* No model number, but a rating: use the rating. A 10,000 mAh and a
           20,000 mAh power bank are two products that differ in nothing BUT
           the rating, and "VZ-POW-10K-POW-BLK" says so where
           "VZ-POW-POW-BLK-02" makes somebody look it up. */
        model ??= tokens.Select(Rating).FirstOrDefault(r => r is not null);

        /* WORD: first plain-letters token that is not the brand, not generic,
           not the category, and not the model. */
        var word = tokens
            .Where(t => t != model)
            .Select(t => Letters.Replace(t, ""))
            .FirstOrDefault(t =>
                t.Length >= 2 &&
                !brandWords.Contains(t) &&
                !Generic.Contains(t) &&
                !categoryWords.Contains(t) &&
                /* the token must have been letters to begin with -- "T9" is a
                   model, and stripping its digit would leave a lone "T". */
                tokens.Any(x => x == t));

        /* Nothing usable as a word (a name that is only a brand and a model, or
           only the category): fall back to the category so the SKU is still
           three letters, never empty. */
        var cat = CategoryCode(categoryName);
        word = string.IsNullOrEmpty(word) ? cat : Take3(word);

        return new Parts(word, model, cat, color);
    }

    private static string Take3(string s) => s.Length <= 3 ? s : s[..3];

    private static readonly Regex WholeRating = new(
        @"^(\d+)(MAH|W|V|A|GB|TB|MM|HZ)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// A rating written the way the trade says it: 10000MAH -> 10K, 5000MAH ->
    /// 5K, 65W -> 65W, 128GB -> 128GB. Whole numbers only -- "1.5M" of cable
    /// would have to lose its point and become a lie.
    /// </summary>
    private static string? Rating(string token)
    {
        var m = WholeRating.Match(token);
        if (!m.Success) return null;

        var n = long.Parse(m.Groups[1].Value);
        var unit = m.Groups[2].Value;

        var code = unit == "MAH" && n >= 1000 && n % 1000 == 0 ? $"{n / 1000}K" : $"{n}{unit}";
        return code.Length <= 8 ? code : null;
    }

    private static HashSet<string> WordsOf(string? s) =>
        (s ?? "").ToUpperInvariant()
            .Split(new[] { ' ', '-', '/', '&', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Letters.Replace(w, ""))
            .Where(w => w.Length > 0)
            .SelectMany(w => new[] { w, w.TrimEnd('S') })   // Earbud / Earbuds
            .ToHashSet();

    /// <summary>First three letters of the category, or GEN for one with none.</summary>
    public static string CategoryCode(string? categoryName)
    {
        var letters = Letters.Replace((categoryName ?? "").ToUpperInvariant(), "");
        return letters.Length == 0 ? "GEN" : Take3(letters);
    }

    /// <summary>
    /// Pulls a colour out of the name and returns it with the name that is left.
    /// "... - Black" wins over a colour word buried mid-name, because a dash
    /// before a colour is the person saying "this is the colour".
    /// </summary>
    private static (string? Color, string Remainder) TakeColor(string raw)
    {
        var parts = raw.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        /* After a dash: the last segment, if every word in it is colour-ish. */
        if (parts.Count > 1)
        {
            var tail = parts[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tail.Length is >= 1 and <= 3 && tail.All(w => Colors.ContainsKey(w) || ColorModifiers.Contains(w))
                && tail.Any(w => Colors.ContainsKey(w)))
            {
                parts.RemoveAt(parts.Count - 1);
                return (ColorCode(tail), string.Join(' ', parts));
            }
        }

        /* Otherwise a colour word anywhere; the last one wins ("Black Case
           For Red Phone" is rare, and last is the likelier "this is the one"). */
        var words = raw.Replace('|', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = words.Count - 1; i >= 0; i--)
        {
            if (!Colors.ContainsKey(words[i])) continue;

            var from = i;
            if (i > 0 && ColorModifiers.Contains(words[i - 1])) from = i - 1;

            var span = words.Skip(from).Take(i - from + 1).ToArray();
            words.RemoveRange(from, i - from + 1);
            return (ColorCode(span), string.Join(' ', words));
        }

        return (null, string.Join(' ', words));
    }

    /* "BLACK" -> BLK.  "SKY BLUE" -> SBLU: the qualifier's first letter in
       front, so two shades of one colour are two colours. */
    private static string ColorCode(string[] words)
    {
        var main = words.Last(w => Colors.ContainsKey(w));
        var code = Colors[main];
        var modifier = words.FirstOrDefault(ColorModifiers.Contains);
        return modifier is null ? code : modifier[0] + code;
    }

    // ══════════════════════════════════════════════════════════════════
    //  SERIALS
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The next free serial for a stem, given every existing SKU that starts
    /// with it. Serials are two digits, three once there are more than 99.
    /// </summary>
    public static string Next(string stem, IEnumerable<string> existingWithStem)
    {
        var highest = 0;
        foreach (var sku in existingWithStem)
        {
            if (!sku.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
            var tail = sku[stem.Length..];
            if (tail.Length > 0 && tail.All(char.IsDigit) && int.TryParse(tail, out var n) && n > highest)
                highest = n;
        }

        var next = highest + 1;
        return stem + next.ToString().PadLeft(2, '0');
    }

    /// <summary>Whether a scanned or typed code is one of OUR SKUs.</summary>
    public static bool LooksLikeSku(string? code) =>
        !string.IsNullOrWhiteSpace(code) && SkuPattern.IsMatch(code.Trim().ToUpperInvariant());
}
