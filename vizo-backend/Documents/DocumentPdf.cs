using System.Globalization;

namespace vizo_backend.Documents;

/// <summary>
/// One renderer for every printable document in the system that is NOT a sale
/// invoice: purchase orders, goods receipts, purchase invoices and returns,
/// stock adjustments and transfers, vouchers, journal entries, expenses, party
/// statements, and all seven reports.
///
/// WHY IT IS GENERIC. Every one of those is the same page: a letterhead, a few
/// labelled facts, a table, sometimes a totals block, sometimes a note. Writing
/// eleven near-identical renderers would mean eleven places to fix when the
/// company address changes. The bill keeps its own renderer
/// (<see cref="InvoicePdf"/>) because it is the document a customer sees and
/// the client cares about its exact shape; everything else goes through here.
///
/// Column widths are WEIGHTS, not points. The caller says "the description
/// column is worth 4 of these and the qty column 1", and the renderer divides
/// the usable page between them -- so a table with three columns and one with
/// eight both fill the page without either being hand-measured.
/// </summary>
public static class DocumentPdf
{
    /* Same palette as the bill and the web app (globals.css). */
    private const string Navy = "#031833";
    private const string NavySoft = "#0A2042";
    private const string Yellow = "#EDC705";
    private const string Ink = "#0F172A";
    private const string Muted = "#64748B";
    private const string Faint = "#94A3B8";
    private const string Hair = "#E2E8F0";
    private const string ZebraFill = "#F7F9FC";
    private const string PanelFill = "#F0F4FA";
    private const string White = "#FFFFFF";
    private const string OnNavy = "#B3C5E1";
    public const string Danger = "#B91C1C";
    public const string Success = "#047857";

    private const double Left = 42;
    private const double Right = 553.28;
    private const double Usable = Right - Left;

    public enum Align { Left, Right, Centre }

    /// <param name="Weight">Share of the table width, relative to the other columns.</param>
    public sealed record Col(string Header, double Weight, Align Align = Align.Left);

    /// <param name="Sub">Small grey second line under the first cell -- a SKU, a code.</param>
    public sealed record Row(IReadOnlyList<string> Cells, string? Sub = null, bool Emphasis = false);

    public sealed record Fact(string Label, string Value);

    public sealed record Party(string Heading, string Name, IReadOnlyList<string> Lines);

    public sealed record Total(string Label, string Value, bool Emphasis = false, string? Colour = null);

    public sealed record LetterHead(
        string Name, string LegalName, string Address, string City, string Country,
        string Phone, string Email, string Ntn, string Strn, string CurrencySymbol);

    /// <summary>
    /// An extra table under the main one. The sales summary needs two -- by day
    /// and by location -- and a report with a single table would have had to
    /// drop half of what the screen shows.
    /// </summary>
    public sealed record Section(string Title, IReadOnlyList<Col> Columns, IReadOnlyList<Row> Rows);

    public sealed record Data(
        LetterHead Company,
        string Title,
        string? DocNo,
        string? StatusName,
        Party? Counterparty,
        IReadOnlyList<Fact> Meta,
        IReadOnlyList<Col> Columns,
        IReadOnlyList<Row> Rows,
        IReadOnlyList<Total> Totals,
        string? Notes,
        string? Footnote,
        string? PreparedBy,
        string? EmptyMessage = null,
        IReadOnlyList<Section>? More = null);

    public static byte[] Render(Data d)
    {
        var pdf = new PdfCanvas();
        var widths = Widths(d.Columns);

        /* Rows are split across pages before anything is drawn, so the footer
           can honestly say "Page 1 of 3". */
        const int firstPageRows = 22;
        const int laterPageRows = 34;
        var pages = Paginate(d.Rows, firstPageRows, laterPageRows);

        for (var p = 0; p < pages.Count; p++)
        {
            if (p > 0) pdf.NewPage();

            var y = p == 0 ? DrawFirstHead(pdf, d) : DrawContinuationHead(pdf, d);
            y = DrawTable(pdf, d, widths, pages[p], y, d.Columns, p == pages.Count - 1);

            if (p == pages.Count - 1)
            {
                foreach (var section in d.More ?? Array.Empty<Section>())
                {
                    /* A second table needs room for its heading, its own header
                       row and at least a couple of lines. If that will not fit
                       above the footer, start it on a fresh page rather than
                       running it off the bottom. */
                    if (y < 190)
                    {
                        pdf.NewPage();
                        y = DrawContinuationHead(pdf, d);
                    }

                    y -= 18;
                    pdf.Text(Left, y, section.Title.ToUpperInvariant(), 8, Faint, bold: true);
                    y -= 6;
                    y = DrawTable(pdf, d, Widths(section.Columns), section.Rows.ToList(),
                        y, section.Columns, isLast: true);
                }

                y = DrawTotals(pdf, d, y);
                DrawNotes(pdf, d, y);
            }
        }

        /* Footers last. A section that overflowed added pages the loop above
           did not know about when it started, so "Page 1 of 1" was printed on
           the first page of a two-page balance sheet. */
        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            DrawFoot(pdf, d, i + 1, pdf.PageCount);
        }

        return pdf.Build();
    }

    private static List<List<Row>> Paginate(IReadOnlyList<Row> rows, int first, int rest)
    {
        var pages = new List<List<Row>>();
        var i = 0;
        while (i < rows.Count || pages.Count == 0)
        {
            var take = pages.Count == 0 ? first : rest;
            pages.Add(rows.Skip(i).Take(take).ToList());
            i += take;
            if (i >= rows.Count) break;
        }
        return pages;
    }

    /// <summary>Turns the column weights into actual point widths.</summary>
    private static double[] Widths(IReadOnlyList<Col> cols)
    {
        var total = cols.Sum(c => c.Weight);
        if (total <= 0) total = cols.Count;
        return cols.Select(c => Usable * (c.Weight / total)).ToArray();
    }

    /* ─────────────────────────── page head ─────────────────────────── */

    private static double DrawFirstHead(PdfCanvas pdf, Data d)
    {
        const double bandHeight = 70;
        var bandBottom = PdfCanvas.A4Height - bandHeight;
        var c = d.Company;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 5, PdfCanvas.A4Width, 5, Yellow);

        pdf.Rect(Left, bandBottom + 22, 28, 28, Yellow);
        pdf.TextCenter(Left + 14, bandBottom + 30,
            c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "V", 17, Navy, bold: true);

        pdf.Text(Left + 38, bandBottom + 39, c.Name, 15, White, bold: true);
        pdf.Text(Left + 38, bandBottom + 27, c.LegalName, 8, OnNavy);

        pdf.TextRight(Right, bandBottom + 41, d.Title.ToUpperInvariant(), 13, Yellow, bold: true);
        if (!string.IsNullOrWhiteSpace(d.DocNo))
            pdf.TextRight(Right, bandBottom + 27, d.DocNo!, 11, White, bold: true);
        if (!string.IsNullOrWhiteSpace(d.StatusName))
            pdf.TextRight(Right, bandBottom + 15, d.StatusName!.ToUpperInvariant(), 7.5, OnNavy, bold: true);

        /* ── seller block, left ── */
        var y = bandBottom - 22;
        pdf.Text(Left, y, "FROM", 7, Faint, bold: true);
        y -= 12;
        pdf.Text(Left, y, c.LegalName, 9, Ink, bold: true);
        y -= 11;
        foreach (var row in CompanyLines(c))
        {
            pdf.Text(Left, y, row, 7.8, Muted);
            y -= 10;
        }

        /* ── meta panel, right ── */
        var panelBottom = y;
        if (d.Meta.Count > 0)
        {
            const double panelLeft = 348;
            var panelTop = bandBottom - 16;
            var panelHeight = 14 + d.Meta.Count * 12;
            pdf.Rect(panelLeft, panelTop - panelHeight, Right - panelLeft, panelHeight, PanelFill);
            pdf.Rect(panelLeft, panelTop - panelHeight, 2.5, panelHeight, Yellow);

            var my = panelTop - 15;
            foreach (var (label, value) in d.Meta.Select(f => (f.Label, f.Value)))
            {
                pdf.Text(panelLeft + 11, my, label, 7.5, Muted);
                pdf.TextRight(Right - 9, my, pdf.Ellipsis(value, 8, Right - panelLeft - 20 - PdfCanvas.Width(label, 7.5), true),
                    8, Ink, bold: true);
                my -= 12;
            }
            panelBottom = Math.Min(panelBottom, panelTop - panelHeight);
        }

        y = panelBottom - 12;

        /* ── counterparty panel ── */
        if (d.Counterparty is { } cp)
        {
            var height = 26 + Math.Max(1, cp.Lines.Count) * 10;
            pdf.Rect(Left, y - height, Usable, height, ZebraFill);
            pdf.Rect(Left, y - height, 3, height, Yellow);

            pdf.Text(Left + 13, y - 14, cp.Heading.ToUpperInvariant(), 7, Faint, bold: true);
            pdf.Text(Left + 13, y - 27, cp.Name, 10.5, Navy, bold: true);

            var py = y - 38;
            foreach (var line in cp.Lines)
            {
                pdf.Text(Left + 13, py, line, 7.8, Muted);
                py -= 10;
            }
            y -= height + 16;
        }

        return y;
    }

    private static double DrawContinuationHead(PdfCanvas pdf, Data d)
    {
        const double bandHeight = 40;
        var bandBottom = PdfCanvas.A4Height - bandHeight;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 4, PdfCanvas.A4Width, 4, Yellow);

        pdf.Text(Left, bandBottom + 15, d.Company.Name, 11, White, bold: true);
        pdf.TextRight(Right, bandBottom + 22, $"{d.Title} {d.DocNo}  (continued)", 9.5, Yellow, bold: true);
        if (d.Counterparty is { } cp)
            pdf.TextRight(Right, bandBottom + 10, cp.Name, 7.5, OnNavy);

        return bandBottom - 26;
    }

    /* ─────────────────────────── the table ─────────────────────────── */

    private static double DrawTable(PdfCanvas pdf, Data d, double[] widths, List<Row> rows,
        double y, IReadOnlyList<Col> cols, bool isLast)
    {
        if (cols.Count == 0) return y;

        const double headHeight = 19;
        pdf.Rect(Left, y - headHeight, Usable, headHeight, NavySoft);

        var ty = y - headHeight + 6;
        var x = Left;
        for (var i = 0; i < cols.Count; i++)
        {
            DrawCell(pdf, cols[i].Header, x, ty, widths[i], cols[i].Align, 7.2, White, bold: true);
            x += widths[i];
        }
        y -= headHeight;

        if (rows.Count == 0)
        {
            pdf.Rect(Left, y - 34, Usable, 34, ZebraFill);
            pdf.TextCenter(Left + Usable / 2, y - 20,
                d.EmptyMessage ?? "Nothing to show for this selection.", 8.5, Faint);
            return y - 34;
        }

        var n = 0;
        foreach (var row in rows)
        {
            /* Two-line rows need the extra height; one-line rows should not
               waste it, because a 40-row report has to fit on a page. */
            var height = row.Sub is null ? 17.0 : 23.0;
            var bottom = y - height;

            if (n % 2 == 1) pdf.Rect(Left, bottom, Usable, height, ZebraFill);

            x = Left;
            var baseline = row.Sub is null ? bottom + 5.5 : bottom + 12;
            for (var i = 0; i < cols.Count && i < row.Cells.Count; i++)
            {
                DrawCell(pdf, row.Cells[i], x, baseline, widths[i], cols[i].Align,
                    8.2, row.Emphasis ? Navy : Ink, row.Emphasis);
                x += widths[i];
            }

            if (row.Sub is not null)
                pdf.Text(Left + 5, bottom + 3.5, pdf.Ellipsis(row.Sub, 6.8, widths[0] - 10), 6.8, Faint);

            pdf.Line(Left, bottom, Right, bottom, Hair, 0.4);
            y = bottom;
            n++;
        }

        return y;
    }

    private static void DrawCell(PdfCanvas pdf, string text, double x, double y, double width,
        Align align, double size, string colour, bool bold)
    {
        const double pad = 5;
        var fitted = pdf.Ellipsis(text, size, width - pad * 2, bold);
        switch (align)
        {
            case Align.Right: pdf.TextRight(x + width - pad, y, fitted, size, colour, bold); break;
            case Align.Centre: pdf.TextCenter(x + width / 2, y, fitted, size, colour, bold); break;
            default: pdf.Text(x + pad, y, fitted, size, colour, bold); break;
        }
    }

    /* ─────────────────────────── totals ─────────────────────────── */

    private static double DrawTotals(PdfCanvas pdf, Data d, double y)
    {
        if (d.Totals.Count == 0) return y;

        const double boxLeft = 330;
        const double step = 14;

        var plain = d.Totals.Where(t => !t.Emphasis).ToList();
        var strong = d.Totals.Where(t => t.Emphasis).ToList();

        y -= 14;

        if (plain.Count > 0)
        {
            var panelHeight = 10 + plain.Count * step;
            pdf.Rect(boxLeft, y - panelHeight, Right - boxLeft, panelHeight, PanelFill);

            var line = y - 16;
            foreach (var t in plain)
            {
                pdf.Text(boxLeft + 10, line, t.Label, 8.2, Muted);
                pdf.TextRight(Right - 10, line, t.Value, 8.6, t.Colour ?? Ink);
                line -= step;
            }
            y -= panelHeight;
        }

        foreach (var t in strong)
        {
            const double bandHeight = 24;
            y -= 6;
            pdf.Rect(boxLeft, y - bandHeight, Right - boxLeft, bandHeight, Navy);
            pdf.Text(boxLeft + 10, y - 16, t.Label.ToUpperInvariant(), 9.5, Yellow, bold: true);
            pdf.TextRight(Right - 10, y - 16, t.Value, 12, White, bold: true);
            y -= bandHeight;
        }

        return y;
    }

    private static void DrawNotes(PdfCanvas pdf, Data d, double y)
    {
        if (string.IsNullOrWhiteSpace(d.Notes)) return;
        /* Left of the totals box, in the space it leaves. */
        var top = Math.Max(y + 60, 110);
        var wrapped = pdf.TextWrapped(Left, top, "NOTE", 7, 270, Faint, bold: true);
        pdf.TextWrapped(Left, wrapped - 2, d.Notes!, 8, 270, Muted);
    }

    /* ─────────────────────────── footer ─────────────────────────── */

    private static void DrawFoot(PdfCanvas pdf, Data d, int pageNo, int pageCount)
    {
        const double y = 58;
        var c = d.Company;

        pdf.Line(Left, y, Right, y, Hair, 0.7);

        if (!string.IsNullOrWhiteSpace(d.Footnote))
            pdf.Text(Left, y - 13, d.Footnote!, 7, Muted);

        pdf.Text(Left, y - 23,
            $"{c.LegalName}  ·  NTN {c.Ntn}  ·  STRN {c.Strn}  ·  {c.Phone}  ·  {c.Email}", 7, Faint);

        pdf.Text(Left, y - 35,
            string.IsNullOrWhiteSpace(d.PreparedBy)
                ? "Computer-generated document. No signature required."
                : $"Prepared by {d.PreparedBy}.  Computer-generated document, no signature required.",
            6.8, Faint);

        pdf.TextRight(Right, y - 35, $"Page {pageNo} of {pageCount}", 6.8, Faint);
        pdf.Rect(0, 0, PdfCanvas.A4Width, 5, Yellow);
    }

    /* ─────────────────────────── helpers ─────────────────────────── */

    private static IEnumerable<string> CompanyLines(LetterHead c)
    {
        if (!string.IsNullOrWhiteSpace(c.Address)) yield return c.Address;

        var place = string.Join(", ", new[] { c.City, c.Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (place.Length > 0) yield return place;

        var contact = string.Join("   ", new[] { c.Phone, c.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (contact.Length > 0) yield return contact;

        var tax = string.Join("   ", new[]
        {
            string.IsNullOrWhiteSpace(c.Ntn) ? null : $"NTN {c.Ntn}",
            string.IsNullOrWhiteSpace(c.Strn) ? null : $"STRN {c.Strn}"
        }.Where(s => s is not null)!);
        if (tax.Length > 0) yield return tax;
    }

    private static readonly CultureInfo Pk = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Money for a table cell: grouped, two decimals, no symbol.</summary>
    public static string Money(decimal v) => v.ToString("N2", Pk);

    /// <summary>Money for a totals line: symbol included.</summary>
    public static string Money(decimal v, string symbol) => $"{symbol} {v.ToString("N2", Pk)}";

    public static string Qty(int v) => v.ToString("N0", Pk);

    public static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public static string Day(DateOnly? d) => d is null ? "-" : Day(d.Value);

    public static string Day(DateTime? d) =>
        d is null ? "-" : d.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
}
