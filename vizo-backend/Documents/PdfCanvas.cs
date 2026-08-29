using System.Globalization;
using System.Text;

namespace vizo_backend.Documents;

/// <summary>
/// A very small PDF writer -- enough for a bill and nothing more.
///
/// WHY THERE IS NO LIBRARY HERE. Every printable-PDF package on NuGet costs
/// something this project should not pay: QuestPDF pulls SkiaSharp native
/// binaries and carries a revenue-tested licence, PDFsharp wants a font
/// resolver per platform, and the HTML-to-PDF ones shell out to a headless
/// browser. An invoice is a page of text, some rules and a few filled
/// rectangles. That is roughly two hundred lines of the PDF spec, written
/// once, with no dependency to keep current and nothing to license.
///
/// It writes PDF 1.4 with the two standard Type1 fonts -- Helvetica and
/// Helvetica-Bold -- which every reader has built in, so nothing is embedded
/// and the file stays around 5 KB.
///
/// Coordinates are PDF's own: origin BOTTOM-left, y grows UPWARDS, units are
/// points (72 to the inch). A4 is 595.28 x 841.89.
///
/// Text is encoded WinAnsi (Latin-1). Anything outside it -- Urdu, an em-dash
/// pasted from Word, a rupee sign -- is folded to the nearest ASCII rather
/// than dropped, because a bill that silently loses a character is worse than
/// one that prints "-".
/// </summary>
public sealed class PdfCanvas
{
    public const double A4Width = 595.28;
    public const double A4Height = 841.89;

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _c;

    public PdfCanvas()
    {
        _c = new StringBuilder();
        _pages.Add(_c);
    }

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _c = new StringBuilder();
        _pages.Add(_c);
    }

    /* ─────────────────────────── drawing ─────────────────────────── */

    public void Rect(double x, double y, double w, double h, string hex)
    {
        var (r, g, b) = Rgb(hex);
        _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n");
        _c.Append(N(x)).Append(' ').Append(N(y)).Append(' ')
          .Append(N(w)).Append(' ').Append(N(h)).Append(" re f\n");
    }

    public void Line(double x1, double y1, double x2, double y2, string hex, double width = 0.6)
    {
        var (r, g, b) = Rgb(hex);
        _c.Append(N(width)).Append(" w\n");
        _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" RG\n");
        _c.Append(N(x1)).Append(' ').Append(N(y1)).Append(" m ")
          .Append(N(x2)).Append(' ').Append(N(y2)).Append(" l S\n");
    }

    public void Text(double x, double y, string? text, double size, string hex = "#000000", bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var (r, g, b) = Rgb(hex);
        _c.Append(N(r)).Append(' ').Append(N(g)).Append(' ').Append(N(b)).Append(" rg\n");
        _c.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(N(size)).Append(" Tf ")
          .Append(N(x)).Append(' ').Append(N(y)).Append(" Td (")
          .Append(Escape(text)).Append(") Tj ET\n");
    }

    /// <summary>Right-aligns on <paramref name="right"/> -- used for every money column.</summary>
    public void TextRight(double right, double y, string? text, double size, string hex = "#000000", bool bold = false)
        => Text(right - Width(text, size, bold), y, text, size, hex, bold);

    public void TextCenter(double centre, double y, string? text, double size, string hex = "#000000", bool bold = false)
        => Text(centre - Width(text, size, bold) / 2, y, text, size, hex, bold);

    /// <summary>
    /// Draws <paramref name="text"/> inside <paramref name="maxWidth"/>, wrapping on
    /// spaces, and returns the y of the line AFTER the last one drawn.
    /// </summary>
    public double TextWrapped(double x, double y, string? text, double size, double maxWidth,
        string hex = "#000000", bool bold = false, double leading = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return y;
        if (leading <= 0) leading = size * 1.25;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();

        foreach (var w in words)
        {
            var probe = line.Length == 0 ? w : $"{line} {w}";
            if (Width(probe, size, bold) > maxWidth && line.Length > 0)
            {
                Text(x, y, line.ToString(), size, hex, bold);
                y -= leading;
                line.Clear().Append(w);
            }
            else
            {
                line.Clear().Append(probe);
            }
        }
        if (line.Length > 0)
        {
            Text(x, y, line.ToString(), size, hex, bold);
            y -= leading;
        }
        return y;
    }

    /// <summary>Cuts a string to fit, ending in an ellipsis. Product names are long.</summary>
    public string Ellipsis(string? text, double size, double maxWidth, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (Width(text, size, bold) <= maxWidth) return text;
        var s = text;
        while (s.Length > 1 && Width(s + "...", size, bold) > maxWidth) s = s[..^1];
        return s.TrimEnd() + "...";
    }

    /* ───────────────────────── measurement ───────────────────────── */

    /* Helvetica and Helvetica-Bold advance widths, ASCII 32..126, per 1000
       units of type size. Straight from the Adobe core-font metrics -- the
       numbers a reader uses, so what we measure is what it draws. */
    private static readonly short[] Regular = {
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,
        556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
        1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,
        667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
        333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,
        556,556,333,500,278,556,500,722,500,500,500,334,260,334,584
    };
    private static readonly short[] Bold = {
        278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,
        556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
        975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,
        667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
        333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,
        611,611,389,556,333,611,556,778,556,556,500,389,280,389,584
    };

    public static double Width(string? text, double size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var table = bold ? Bold : Regular;
        double total = 0;
        foreach (var raw in Fold(text))
        {
            var c = (int)raw;
            total += c is >= 32 and <= 126 ? table[c - 32] : 556;
        }
        return total * size / 1000.0;
    }

    /* ───────────────────────── encoding ───────────────────────── */

    /// <summary>
    /// Folds the characters a Windows keyboard and a copy-paste actually
    /// produce down to something WinAnsi can carry.
    /// </summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            /* Compared as code points rather than character literals: this
               source file then holds nothing but ASCII, so it reads the same
               whatever encoding a compiler or an editor decides to assume. */
            sb.Append((int)c switch
            {
                0x2013 or 0x2014 or 0x2212 => '-',    // en dash, em dash, minus
                0x2018 or 0x2019 or 0x02BC => '\'',   // curly single quotes
                0x201C or 0x201D           => '"',    // curly double quotes
                0x2022 or 0x00B7           => '-',    // bullet, middle dot
                0x2026                     => '.',    // ellipsis
                0x00A0                     => ' ',    // non-breaking space
                0x20A8 or 0x20B9           => 'R',    // rupee signs
                _ => c <= 0xFF ? c : '?'
            });
        }
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in Fold(s))
        {
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static (double r, double g, double b) Rgb(string hex)
    {
        hex = hex.TrimStart('#');
        return (
            Convert.ToInt32(hex[..2], 16) / 255.0,
            Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0,
            Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0);
    }

    private static string N(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /* ───────────────────────── serialisation ───────────────────────── */

    /// <summary>
    /// Assembles the finished file. Objects are numbered in a fixed order:
    ///   1 catalog · 2 page tree · 3 Helvetica · 4 Helvetica-Bold
    ///   then, per page, the page object and its content stream.
    /// The cross-reference table needs every object's byte offset, so the
    /// whole thing is written into one buffer and the offsets recorded as
    /// they are reached.
    /// </summary>
    public byte[] Build()
    {
        var latin1 = Encoding.Latin1;
        var buf = new MemoryStream();
        var offsets = new List<long> { 0 }; // object 0 is the free head

        void Write(string s)
        {
            var bytes = latin1.GetBytes(s);
            buf.Write(bytes, 0, bytes.Length);
        }
        void Obj(int _, string body)
        {
            offsets.Add(buf.Position);
            Write(body);
        }

        /* The four high bytes on the second line are the marker the spec asks
           for so a transfer program treats the file as binary. Written as
           escapes, not pasted, so this source stays pure ASCII. */
        Write("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");

        var pageCount = _pages.Count;
        // page object i -> 5 + 2i, its content stream -> 6 + 2i
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{5 + 2 * i} 0 R"));

        Obj(1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2, $"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");
        Obj(3, "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Obj(4, "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageNo = 5 + 2 * i;
            var streamNo = 6 + 2 * i;
            var content = _pages[i].ToString();
            var length = latin1.GetByteCount(content);

            Obj(pageNo,
                $"{pageNo} 0 obj\n<< /Type /Page /Parent 2 0 R " +
                $"/MediaBox [0 0 {N(A4Width)} {N(A4Height)}] " +
                "/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> " +
                $"/Contents {streamNo} 0 R >>\nendobj\n");

            Obj(streamNo, $"{streamNo} 0 obj\n<< /Length {length} >>\nstream\n{content}endstream\nendobj\n");
        }

        var xref = buf.Position;
        var total = offsets.Count;
        Write($"xref\n0 {total}\n");
        Write("0000000000 65535 f \n");
        for (var i = 1; i < total; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {total} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return buf.ToArray();
    }
}
