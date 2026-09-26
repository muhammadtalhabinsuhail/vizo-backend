using System.Globalization;

namespace vizo_backend.Documents;

/// <summary>
/// A statement of account on paper -- a customer's, or a member of staff's.
///
/// ─────────────────────────────── WHY ITS OWN LAYOUT ─────────────────────────────
///
/// The owner's old system printed this page for thirty years, and the shops
/// know how to read it: a From-To range, the account's code and name, "Balance
/// B/F" at the start date, every sale on ONE debit line with its items indented
/// underneath it (item, qty, rate, amount), every receipt as "RV-no, bank" on a
/// credit line, a running balance, and a totals row carrying the entry count and
/// the "Ledger Limit". The four sample statements it produced are the brief.
///
/// DocumentPdf -- the engine behind every other document here -- draws one row
/// per line and has no notion of a row that owns a block of smaller rows under
/// it, which is the whole point of this page. So this file draws its own table,
/// with the SAME hand-built PdfCanvas, the SAME palette as the bill and every
/// other document (navy, the brand yellow, the slate greys -- copied from
/// DocumentPdf/InvoicePdf, not re-invented), the same letterhead band and the
/// same footer. It looks like the rest of this system, not like FoxPro; it READS
/// like the page the shops already know.
///
/// Pagination is by height, not by row count: a sale with twelve items is
/// thirteen rows tall, and a page that splits one away from its items is a page
/// nobody can check. A row and its items move to the next page together
/// whenever they fit on one.
/// </summary>
public static class LedgerStatementPdf
{
    /* Same palette as DocumentPdf / InvoicePdf / globals.css. */
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
    private const string Danger = "#B91C1C";

    private const double Left = 42;
    private const double Right = 553.28;
    private const double Usable = Right - Left;
    private const double Bottom = 84;          // above the footer

    /* Column edges. Particulars takes what the four fixed columns leave. */
    private const double DateW = 54;
    private const double MoneyW = 64;
    private const double BalanceW = 74;
    private static double ParticularsW => Usable - DateW - MoneyW * 2 - BalanceW;

    public sealed record Item(string Name, decimal Qty, decimal Rate, decimal Amount);

    /// <param name="Tag">A short marker drawn after the particulars -- "Reversed", "Manual".</param>
    public sealed record Line(DateOnly? Date, string Particulars, decimal Debit, decimal Credit,
        decimal Balance, IReadOnlyList<Item> Items, bool Emphasis = false, string? Tag = null);

    public sealed record Data(
        DocumentPdf.LetterHead Company,
        string Title,
        string AccountCode,
        string AccountName,
        IReadOnlyList<string> AccountLines,
        DateOnly From,
        DateOnly To,
        string LimitLabel,
        decimal? Limit,
        IReadOnlyList<Line> Lines,
        int EntryCount,
        decimal TotalDebit,
        decimal TotalCredit,
        decimal Closing,
        string ClosingLabel,
        string? Footnote,
        bool ItemColumns = true);

    public static byte[] Render(Data d)
    {
        var pdf = new PdfCanvas();
        var y = DrawFirstHead(pdf, d);
        y = DrawTableHead(pdf, y, d.ItemColumns);

        var n = 0;
        foreach (var line in d.Lines)
        {
            var need = RowHeight(line);
            var room = y - Bottom;
            var fullPage = PdfCanvas.A4Height - 66 - 19 - Bottom - 40;
            if (need > room && (need <= fullPage || room < 40))
            {
                pdf.NewPage();
                y = DrawContinuationHead(pdf, d);
                y = DrawTableHead(pdf, y, d.ItemColumns);
                n = 0;
            }
            y = DrawRow(pdf, line, y, n++ % 2 == 1, () =>
            {
                pdf.NewPage();
                var top = DrawContinuationHead(pdf, d);
                return DrawTableHead(pdf, top, d.ItemColumns);
            });
        }

        if (d.Lines.Count == 0)
        {
            pdf.Rect(Left, y - 30, Usable, 30, ZebraFill);
            pdf.TextCenter(Left + Usable / 2, y - 18, "No activity in this period.", 8.5, Faint);
            y -= 30;
        }

        if (y - 60 < Bottom)
        {
            pdf.NewPage();
            y = DrawContinuationHead(pdf, d);
        }
        DrawTotals(pdf, d, y);

        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            DrawFoot(pdf, d, i + 1, pdf.PageCount);
        }
        return pdf.Build();
    }

    /* ─────────────────────────── heads ─────────────────────────── */

    private static double DrawFirstHead(PdfCanvas pdf, Data d)
    {
        const double band = 70;
        var bandBottom = PdfCanvas.A4Height - band;
        var c = d.Company;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, band, Navy);
        pdf.Rect(0, bandBottom - 5, PdfCanvas.A4Width, 5, Yellow);

        pdf.Rect(Left, bandBottom + 22, 28, 28, Yellow);
        pdf.TextCenter(Left + 14, bandBottom + 30, c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "V", 17, Navy, bold: true);
        pdf.Text(Left + 38, bandBottom + 39, c.Name, 15, White, bold: true);
        pdf.Text(Left + 38, bandBottom + 27, c.LegalName, 8, OnNavy);

        pdf.TextRight(Right, bandBottom + 41, d.Title.ToUpperInvariant(), 13, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 26, $"FROM {Day(d.From)}  TO  {Day(d.To)}", 9, White, bold: true);
        pdf.TextRight(Right, bandBottom + 13, $"Printed {Services.BusinessClock.Now():dd MMM yyyy, h:mm tt}", 7, OnNavy);

        /* ── the account strip: code, name, and the limit on the right ── */
        var top = bandBottom - 18;
        var height = 34 + Math.Max(0, d.AccountLines.Count) * 10;
        pdf.Rect(Left, top - height, Usable, height, PanelFill);
        pdf.Rect(Left, top - height, 3, height, Yellow);

        pdf.Text(Left + 13, top - 13, "A/C CODE", 6.8, Faint, bold: true);
        pdf.Text(Left + 13, top - 26, d.AccountCode, 10.5, Navy, bold: true);

        const double nameX = Left + 110;
        pdf.Text(nameX, top - 13, "A/C NAME", 6.8, Faint, bold: true);
        pdf.Text(nameX, top - 26, pdf.Ellipsis(d.AccountName, 10.5, 260, true), 10.5, Navy, bold: true);
        var ly = top - 37;
        foreach (var l in d.AccountLines)
        {
            pdf.Text(nameX, ly, pdf.Ellipsis(l, 7.6, 260), 7.6, Muted);
            ly -= 10;
        }

        pdf.TextRight(Right - 12, top - 13, d.LimitLabel.ToUpperInvariant(), 6.8, Faint, bold: true);
        pdf.TextRight(Right - 12, top - 26,
            d.Limit is null ? "-" : d.Limit.Value > 0 ? Money(d.Limit.Value) : "No limit", 10.5, Navy, bold: true);

        return top - height - 12;
    }

    private static double DrawContinuationHead(PdfCanvas pdf, Data d)
    {
        const double band = 40;
        var bandBottom = PdfCanvas.A4Height - band;
        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, band, Navy);
        pdf.Rect(0, bandBottom - 4, PdfCanvas.A4Width, 4, Yellow);
        pdf.Text(Left, bandBottom + 15, d.Company.Name, 11, White, bold: true);
        pdf.TextRight(Right, bandBottom + 22, $"{d.Title} {d.AccountCode}  (continued)", 9.5, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 10, d.AccountName, 7.5, OnNavy);
        return bandBottom - 22;
    }

    private static double DrawTableHead(PdfCanvas pdf, double y, bool itemColumns)
    {
        const double h = 19;
        pdf.Rect(Left, y - h, Usable, h, NavySoft);
        var ty = y - h + 6.5;
        pdf.Text(Left + 5, ty, "DATE", 7.2, White, bold: true);
        pdf.Text(Left + DateW + 5, ty, "PARTICULARS", 7.2, White, bold: true);
        if (itemColumns)
        {
            /* The item block under a sale: qty, rate and amount sit at the
               right of PARTICULARS, where the old statement had them. */
            var itemsRight = Left + DateW + ParticularsW - 6;
            pdf.TextRight(itemsRight - 82, ty, "QTY", 6.4, OnNavy, bold: true);
            pdf.TextRight(itemsRight - 44, ty, "RATE", 6.4, OnNavy, bold: true);
            pdf.TextRight(itemsRight, ty, "AMOUNT", 6.4, OnNavy, bold: true);
        }
        var x = Left + DateW + ParticularsW;
        pdf.TextRight(x + MoneyW - 5, ty, "DEBIT", 7.2, White, bold: true);
        pdf.TextRight(x + MoneyW * 2 - 5, ty, "CREDIT", 7.2, White, bold: true);
        pdf.TextRight(Right - 5, ty, "BALANCE", 7.2, White, bold: true);
        return y - h;
    }

    /* ─────────────────────────── rows ─────────────────────────── */

    private const double MainH = 16.5;
    private const double ItemH = 11;

    private static double RowHeight(Line l) => MainH + (l.Items.Count > 0 ? l.Items.Count * ItemH + 3 : 0);

    private static double DrawRow(PdfCanvas pdf, Line l, double y, bool zebra, Func<double> newPage)
    {
        var bottom = y - MainH;
        if (zebra || l.Emphasis) pdf.Rect(Left, bottom, Usable, MainH, l.Emphasis ? PanelFill : ZebraFill);

        var baseline = bottom + 5;
        var ink = l.Emphasis ? Navy : Ink;
        pdf.Text(Left + 5, baseline, l.Date is null ? "" : Short(l.Date.Value), 8, ink, l.Emphasis);

        var partX = Left + DateW + 5;
        var partMax = ParticularsW - 10 - (l.Tag is null ? 0 : PdfCanvas.Width(l.Tag, 6.5, true) + 12);
        var text = pdf.Ellipsis(l.Particulars, 8.2, partMax, l.Emphasis);
        pdf.Text(partX, baseline, text, 8.2, ink, l.Emphasis);
        if (l.Tag is not null)
        {
            var tx = partX + PdfCanvas.Width(text, 8.2, l.Emphasis) + 6;
            pdf.Text(tx, baseline + 0.5, l.Tag.ToUpperInvariant(), 6.5, l.Tag.StartsWith("Rev") ? Danger : Muted, bold: true);
        }

        var x = Left + DateW + ParticularsW;
        pdf.TextRight(x + MoneyW - 5, baseline, Money(l.Debit), 8.2, ink, l.Emphasis);
        pdf.TextRight(x + MoneyW * 2 - 5, baseline, Money(l.Credit), 8.2, ink, l.Emphasis);
        pdf.TextRight(Right - 5, baseline, Money(l.Balance), 8.2, l.Balance < 0 ? Danger : Navy, bold: true);
        y = bottom;

        if (l.Items.Count > 0)
        {
            var itemsLeft = Left + DateW + 8;
            var itemsRight = Left + DateW + ParticularsW - 6;
            var barTop = y;
            foreach (var it in l.Items)
            {
                if (y - ItemH < Bottom)
                {
                    pdf.Rect(itemsLeft - 3, y, 1.4, barTop - y - 1, Yellow);
                    y = newPage();
                    barTop = y;
                }
                var b = y - ItemH + 3;
                pdf.Text(itemsLeft + 4, b, pdf.Ellipsis(it.Name, 7, itemsRight - itemsLeft - 108), 7, Muted);
                pdf.TextRight(itemsRight - 82, b, Qty(it.Qty), 7, Muted);
                pdf.TextRight(itemsRight - 44, b, Money(it.Rate), 7, Muted);
                pdf.TextRight(itemsRight, b, Money(it.Amount), 7, Ink);
                y -= ItemH;
            }
            y -= 3;
            pdf.Rect(itemsLeft - 3, y + 2, 1.4, barTop - y - 3, Yellow);
        }

        pdf.Line(Left, y, Right, y, Hair, 0.4);
        return y;
    }

    /* ─────────────────────────── totals ─────────────────────────── */

    private static void DrawTotals(PdfCanvas pdf, Data d, double y)
    {
        const double h = 26;
        y -= 4;
        pdf.Rect(Left, y - h, Usable, h, Navy);
        pdf.Rect(Left, y - h, 3, h, Yellow);
        var b = y - 16.5;
        pdf.Text(Left + 10, b, "TOTALS", 8, Yellow, bold: true);
        pdf.Text(Left + 52, b, $"{d.EntryCount} {(d.EntryCount == 1 ? "entry" : "entries")}", 8, White, bold: true);
        pdf.Text(Left + 130, b, $"{d.LimitLabel}: {(d.Limit is null ? "-" : Money(d.Limit.Value))}", 7.5, OnNavy);

        var x = Left + DateW + ParticularsW;
        pdf.TextRight(x + MoneyW - 5, b, Money(d.TotalDebit), 8.4, White, bold: true);
        pdf.TextRight(x + MoneyW * 2 - 5, b, Money(d.TotalCredit), 8.4, White, bold: true);
        pdf.TextRight(Right - 5, b, Money(d.Closing), 9.5, Yellow, bold: true);

        y -= h + 14;
        pdf.TextRight(Right, y, $"{d.ClosingLabel}:  {d.Company.CurrencySymbol} {Money(d.Closing)}", 10, Navy, bold: true);
    }

    private static void DrawFoot(PdfCanvas pdf, Data d, int pageNo, int pageCount)
    {
        const double y = 58;
        var c = d.Company;
        pdf.Line(Left, y, Right, y, Hair, 0.7);
        if (!string.IsNullOrWhiteSpace(d.Footnote)) pdf.Text(Left, y - 13, d.Footnote!, 7, Muted);
        pdf.Text(Left, y - 23, $"{c.LegalName}  ·  NTN {c.Ntn}  ·  STRN {c.Strn}  ·  {c.Phone}  ·  {c.Email}", 7, Faint);
        pdf.Text(Left, y - 35, "Computer-generated statement. No signature required.", 6.8, Faint);
        pdf.TextRight(Right, y - 35, $"Page {pageNo} of {pageCount}", 6.8, Faint);
        pdf.Rect(0, 0, PdfCanvas.A4Width, 5, Yellow);
    }

    /* ─────────────────────────── helpers ─────────────────────────── */

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Whole rupees print without ".00" in the running columns, as the old statement did; paisa are kept when present.</summary>
    private static string Money(decimal v)
    {
        var s = Math.Abs(v).ToString(v == Math.Round(v) ? "N0" : "N2", En);
        return v < 0 ? $"-{s}" : s;
    }

    private static string Qty(decimal v) => v.ToString(v == Math.Round(v) ? "N0" : "N2", En);

    private static string Short(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Day(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
}
