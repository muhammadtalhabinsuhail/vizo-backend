using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace vizo_backend.Documents;

/// <summary>
/// The till slip: one sale invoice as an 80 mm thermal-printer receipt.
///
/// WHY A SECOND DOCUMENT FOR THE SAME INVOICE. The owner (2 October): the
/// order page's "Print Bill" printed the A4 invoice, which is right for the
/// file and wrong for the counter -- the shop hands the customer a slip off
/// the POS printer, and an A4 page squeezed onto a 72 mm roll comes out as
/// unreadable grey. So the A4 button became "Print Invoice" (unchanged in what
/// it prints, see InvoicePdf) and "Print Bill" is now this: only the major
/// details, one long narrow page, nothing a cashier would not read aloud.
///
/// THE PAGE. 80 mm paper is 226.77 pt wide; the print head covers about
/// 72 mm of it, so everything is drawn inside a 72 mm column with 4 mm either
/// side. The HEIGHT IS THE CONTENT: a roll has no page length, and an A4-tall
/// page would feed half a metre of blank paper after a two-line sale. The
/// layout is therefore run twice -- once to measure, once to draw -- and the
/// page is cut to exactly what was drawn.
///
/// THE FONT IS COURIER. A receipt is a monospaced document: columns line up by
/// character count, the dashed rules are literally dashes, and that is what
/// people recognise as a till slip. Courier is one of the fourteen fonts every
/// PDF reader has built in, so like Helvetica it embeds nothing.
///
/// HOW THIS USES PdfCanvas WITHOUT CHANGING IT. PdfCanvas is written for A4
/// and Helvetica: the MediaBox and the two font objects are fixed inside
/// Build(). Other documents depend on exactly that, so rather than widen its
/// API under them, this file draws through it as usual and then edits the
/// finished bytes: the MediaBox becomes 80 mm x the measured height, the two
/// font objects become Courier and Courier-Bold, and the cross-reference table
/// is rebuilt because both edits move every later object's byte offset. A
/// wrong xref offset is a file some readers refuse, so the offsets are
/// re-derived from the bytes rather than nudged by a computed delta.
///
/// All text is measured with Courier's metric (every glyph 600/1000 em), NOT
/// PdfCanvas.Width, which knows only Helvetica. PdfCanvas.Text is used purely
/// to place the string; it still folds Urdu, curly quotes and the rupee sign
/// to ASCII the same way the A4 bill does, one character for one, so a
/// character count measured here is the count it draws.
///
/// Like InvoicePdf, nothing is invented: a value missing from the row is a
/// line left out, never a placeholder.
/// </summary>
public static class ReceiptPdf
{
    /* 80 mm roll, 72 mm printable. 1 mm = 72 / 25.4 pt. */
    public const double PageWidth = 80 * 72 / 25.4;          // 226.77
    private const double Margin = 4 * 72 / 25.4;             // 11.34
    private const double Left = Margin;
    private const double Right = PageWidth - Margin;
    private const double Column = Right - Left;              // 204.09 -- 72 mm
    private const double Centre = PageWidth / 2;

    /* Body size 8 pt gives 42 characters across 72 mm (8 x 0.6 = 4.8 pt per
       character), the classic Font-A width of an 80 mm POS printer, so the
       slip reads the way a till slip is expected to read. */
    private const double Body = 8;
    private const double Small = 7;
    private const double Lead = Body * 1.3;

    /* Thermal paper has one colour. Pure black throughout: a grey on a
       thermal head is a dither pattern, which is lighter and fuzzier than the
       grey on screen, so it is simply not used. */
    private const string Ink = "#000000";

    private const double TopPad = 8;
    private const double BottomPad = 14;   // room for the cutter, so the footer is not sliced

    private static readonly CultureInfo Pk = CultureInfo.GetCultureInfo("en-US");

    public sealed record Line(string Name, int Qty, decimal Rate, decimal LineTotal);

    /// <summary>
    /// Only what goes on the slip. Leaner than InvoicePdf.Data on purpose: the
    /// receipt does not print NTN, STRN, address blocks or the due date, and a
    /// record that carries them invites somebody to add them back.
    /// </summary>
    public sealed record Data(
        string CompanyName, string? CompanyPhone, string? CompanyCity, string CurrencySymbol,
        string InvoiceNo, DateOnly InvoiceDate, string? OrderNo,
        string CustomerName, string? CustomerPhone, string? CustomerCity,
        string? Salesman, string PaymentMethod,
        decimal Subtotal, decimal Discount, decimal Tax, decimal Total,
        decimal Paid, decimal Balance,
        IReadOnlyList<Line> Lines,
        DateTime? PrintedAt = null);

    public static byte[] Render(Data d)
    {
        /* Pass one measures. Drawn from an arbitrary top far above anything
           real; only the distance travelled matters. */
        const double probeTop = 100_000;
        var bottom = Draw(new PdfCanvas(), d, probeTop);
        var height = Math.Ceiling(TopPad + (probeTop - bottom) + BottomPad);

        /* Pass two draws for real, with the first line TopPad below the top
           edge of a page exactly `height` tall. */
        var pdf = new PdfCanvas();
        Draw(pdf, d, height - TopPad);
        return Reshape(pdf.Build(), PageWidth, height);
    }

    /* ─────────────────────────── layout ─────────────────────────── */

    /// <summary>Draws the slip from <paramref name="top"/> down; returns the y it ended at.</summary>
    private static double Draw(PdfCanvas pdf, Data d, double top)
    {
        var y = top;
        var cols = Cols(Body);
        var cur = string.IsNullOrWhiteSpace(d.CurrencySymbol) ? "PKR" : d.CurrencySymbol.Trim();

        // ── the shop ──
        y -= 12;
        foreach (var part in Wrap(d.CompanyName.ToUpperInvariant(), Cols(13)))
        {
            pdf.Text(Centre - W(part, 13) / 2, y, part, 13, Ink, bold: true);
            y -= 15;
        }
        var shopLine = Join(" | ", d.CompanyCity, string.IsNullOrWhiteSpace(d.CompanyPhone) ? null : "Tel " + d.CompanyPhone);
        if (shopLine.Length > 0)
        {
            foreach (var part in Wrap(shopLine, Cols(Small)))
            {
                pdf.Text(Centre - W(part, Small) / 2, y, part, Small, Ink);
                y -= Small * 1.3;
            }
        }
        y -= 2;
        y = Rule(pdf, y, '-');

        // ── the document ──
        pdf.Text(Centre - W("SALE RECEIPT", 9) / 2, y, "SALE RECEIPT", 9, Ink, bold: true);
        y -= Lead + 2;

        y = Pair(pdf, y, "Bill No", d.InvoiceNo, bold: true);
        y = Pair(pdf, y, "Date", Day(d.InvoiceDate));
        if (!string.IsNullOrWhiteSpace(d.OrderNo)) y = Pair(pdf, y, "Order No", d.OrderNo!);
        y = Labelled(pdf, y, "Customer", d.CustomerName, bold: true);
        if (!string.IsNullOrWhiteSpace(d.CustomerPhone)) y = Labelled(pdf, y, "Phone", d.CustomerPhone!);
        if (!string.IsNullOrWhiteSpace(d.CustomerCity)) y = Labelled(pdf, y, "City", d.CustomerCity!);
        if (!string.IsNullOrWhiteSpace(d.Salesman)) y = Labelled(pdf, y, "Salesman", d.Salesman!);
        y = Rule(pdf, y, '-');

        // ── the lines ──
        /* Header in the same columns the figures use below it: the item name
           on its own line(s), then "qty x rate" on the left and the amount on
           the right. Two-line items are what every 80 mm slip does, because a
           product name and three numbers do not fit in 42 characters. */
        pdf.Text(Left, y, "Item", Body, Ink, bold: true);
        RightText(pdf, y, "Amount", Body, bold: true);
        y -= Lead;
        pdf.Text(Left, y, "Qty x Rate", Small, Ink);
        y -= Lead - 1;
        y = Rule(pdf, y, '-');

        var totalQty = 0;
        foreach (var l in d.Lines)
        {
            foreach (var part in Wrap(l.Name, cols))
            {
                pdf.Text(Left, y, part, Body, Ink, bold: true);
                y -= Lead;
            }
            pdf.Text(Left + W("  ", Body), y, $"{l.Qty.ToString("N0", Pk)} x {Money(l.Rate)}", Body, Ink);
            RightText(pdf, y, Money(l.LineTotal), Body);
            y -= Lead + 2;
            totalQty += l.Qty;
        }
        y += 2;
        y = Rule(pdf, y, '-');
        pdf.Text(Left, y, $"Items: {d.Lines.Count}", Body, Ink);
        RightText(pdf, y, $"Qty: {totalQty.ToString("N0", Pk)}", Body);
        y -= Lead;

        // ── the money ──
        /* Subtotal is only worth a line when something stands between it and
           the total; on a plain sale it would just print the total twice. */
        var adjusted = d.Discount != 0 || d.Tax != 0;
        if (adjusted)
        {
            y = Rule(pdf, y, '-');
            y = Amount(pdf, y, "Subtotal", Money(d.Subtotal));
            if (d.Discount != 0) y = Amount(pdf, y, "Discount", "-" + Money(Math.Abs(d.Discount)));
            if (d.Tax != 0) y = Amount(pdf, y, "Tax", Money(d.Tax));
        }

        y = Rule(pdf, y, '=');
        y -= 2;
        pdf.Text(Left, y, "TOTAL", 11, Ink, bold: true);
        var total = $"{cur} {Money(d.Total)}";
        pdf.Text(Right - W(total, 11), y, total, 11, Ink, bold: true);
        y -= 11 * 1.3;
        y = Rule(pdf, y, '=');

        if (d.Paid != 0)
        {
            y = Amount(pdf, y, "Paid", Money(d.Paid));
            if (d.Balance != 0) y = Amount(pdf, y, "Balance", Money(d.Balance), bold: true);
        }
        y = Amount(pdf, y, "Payment", Pretty(d.PaymentMethod));
        y = Rule(pdf, y, '-');

        // ── the foot ──
        y -= 2;
        const string thanks = "Thank you for your business!";
        pdf.Text(Centre - W(thanks, Body) / 2, y, thanks, Body, Ink, bold: true);
        y -= Body * 1.5;
        var printed = (d.PrintedAt ?? DateTime.Now).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        var stamp = $"Printed {printed}";
        pdf.Text(Centre - W(stamp, Small) / 2, y, stamp, Small, Ink);
        y -= Small;

        return y;
    }

    /* "Label    value", value right-aligned. For the short fields. */
    private static double Pair(PdfCanvas pdf, double y, string label, string value, bool bold = false)
    {
        pdf.Text(Left, y, label, Body, Ink);
        RightText(pdf, y, value, Body, bold);
        return y - Lead;
    }

    /* "Label:   value that may wrap" -- the value hangs at a fixed indent so a
       long customer name wraps under itself, not under the label. */
    private static double Labelled(PdfCanvas pdf, double y, string label, string value, bool bold = false)
    {
        const int indent = 10;   // "Customer: " -- the longest label, plus colon and space
        var x = Left + indent * Body * 0.6;
        pdf.Text(Left, y, label + ":", Body, Ink);
        foreach (var part in Wrap(value, Cols(Body) - indent))
        {
            pdf.Text(x, y, part, Body, Ink, bold);
            y -= Lead;
        }
        return y;
    }

    private static double Amount(PdfCanvas pdf, double y, string label, string value, bool bold = false)
    {
        pdf.Text(Left, y, label, Body, Ink, bold);
        RightText(pdf, y, value, Body, bold);
        return y - Lead;
    }

    /* A rule made of the character itself, the full 42 columns. Drawn as text
       rather than a line because that is what a receipt's rule IS, and a
       0.6 pt vector line is thinner than one dot on a 203 dpi head. */
    private static double Rule(PdfCanvas pdf, double y, char c)
    {
        pdf.Text(Left, y, new string(c, Cols(Body)), Body, Ink);
        return y - Lead;
    }

    private static void RightText(PdfCanvas pdf, double y, string text, double size, bool bold = false)
        => pdf.Text(Right - W(text, size), y, text, size, Ink, bold);

    /* ─────────────────────── Courier measurement ─────────────────────── */

    private static double W(string? text, double size) => (text?.Length ?? 0) * size * 0.6;

    /* How many characters fit across the 72 mm column at this size. */
    private static int Cols(double size) => (int)Math.Floor(Column / (size * 0.6) + 1e-6);

    /// <summary>
    /// Word-wraps to <paramref name="cols"/> characters, breaking a single word
    /// that is longer than a whole line (an SKU-like product name with no
    /// spaces) rather than letting it run off the paper.
    /// </summary>
    private static List<string> Wrap(string? text, int cols)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        var line = new StringBuilder();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            while (word.Length > cols)
            {
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                lines.Add(word[..cols]);
                word = word[cols..];
            }
            if (line.Length == 0) line.Append(word);
            else if (line.Length + 1 + word.Length <= cols) line.Append(' ').Append(word);
            else { lines.Add(line.ToString()); line.Clear().Append(word); }
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    private static string Join(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private static string Money(decimal v) => v.ToString("N2", Pk);
    private static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /* Same keys InvoicePdf prettifies, except CREDIT: at the counter the word
       people use is "Credit", and the slip is read at the counter. */
    private static string Pretty(string key) =>
        key switch
        {
            "CASH" => "Cash",
            "BANK" => "Bank transfer",
            "CREDIT" => "Credit",
            "CHEQUE" => "Cheque",
            "JAZZCASH" => "JazzCash",
            "EASYPAISA" => "Easypaisa",
            "CREDIT_NOTE" => "Credit note",
            "PETTY_CASH" => "Petty cash",
            _ => key
        };

    /* ─────────────────────── reshaping PdfCanvas output ─────────────────────── */

    /// <summary>
    /// Turns PdfCanvas's A4 / Helvetica file into an 80 mm x <paramref name="height"/>
    /// Courier one, then rebuilds the xref table and startxref.
    ///
    /// Safe to do textually because PdfCanvas writes Latin-1 (one byte per
    /// char, so a string index IS a byte offset), puts each "N 0 obj" at the
    /// start of a line, and this receipt embeds no images -- every byte in the
    /// file is text. Content streams cannot contain a line that starts with
    /// "N 0 obj": every content line begins with an operand or BT/q.
    /// </summary>
    private static byte[] Reshape(byte[] a4, double width, double height)
    {
        var latin1 = Encoding.Latin1;
        var s = latin1.GetString(a4);

        string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

        s = Regex.Replace(s, @"/MediaBox \[[^\]]*\]", $"/MediaBox [0 0 {N(width)} {N(height)}]");
        s = s.Replace("/BaseFont /Helvetica-Bold ", "/BaseFont /Courier-Bold ")
             .Replace("/BaseFont /Helvetica ", "/BaseFont /Courier ");

        var xrefAt = s.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        var body = s[..xrefAt];

        var offsets = new SortedDictionary<int, int>();
        foreach (Match m in Regex.Matches(body, @"(?m)^(\d+) 0 obj$"))
            offsets[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = m.Index;

        var size = offsets.Keys.Max() + 1;
        var sb = new StringBuilder(body);
        sb.Append("xref\n0 ").Append(size).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < size; i++)
            sb.Append(offsets.TryGetValue(i, out var at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xrefAt).Append("\n%%EOF\n");

        return latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A receipt with made-up but realistic data, for showing the design
    /// before it is wired to a real invoice. Not called by the API.
    /// </summary>
    public static byte[] Sample() => Render(new Data(
        CompanyName: "VIZO Pakistan",
        CompanyPhone: "0300 1234567",
        CompanyCity: "Lahore",
        CurrencySymbol: "PKR",
        InvoiceNo: "INV-26-8897",
        InvoiceDate: new DateOnly(2026, 10, 2),
        OrderNo: "ORD-26-0177",
        CustomerName: "Saif Ullah - Lifestyle International",
        CustomerPhone: "0321 7654321",
        CustomerCity: "Faisalabad",
        Salesman: "Muhammad Ammar Kamran",
        PaymentMethod: "CREDIT",
        Subtotal: 15_400m, Discount: 0m, Tax: 0m, Total: 15_400m,
        Paid: 0m, Balance: 15_400m,
        Lines: new[]
        {
            new Line("VIZO Titan T9 Wireless Earbuds - Black", 10, 1_050m, 10_500m),
            new Line("VIZO Titan T9 Wireless Earbuds - White", 5, 980m, 4_900m),
        },
        PrintedAt: new DateTime(2026, 10, 2, 14, 5, 0)));
}
