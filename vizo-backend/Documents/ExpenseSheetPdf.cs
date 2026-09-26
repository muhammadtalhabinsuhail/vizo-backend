using System.Globalization;
using Microsoft.EntityFrameworkCore;
using vizo_backend.Models;

namespace vizo_backend.Documents;

/// <summary>
/// The day's expense invoice: every expense one location paid out on one
/// date, on one document.
///
/// WHY IT HAS ITS OWN RENDERER. The owner's words were "one invoice per day",
/// and they want it to look like one -- the sales invoice's palette, a heading
/// that says which day and which drawer, the lines, what each cash or bank
/// account paid out, who prepared it and who approved it. The generic
/// <see cref="DocumentPdf"/> layout has one table and one totals box and no
/// place for either the paid-from breakdown or two signatures, so this page is
/// drawn here on the same <see cref="PdfCanvas"/>, with the colours copied
/// from <see cref="InvoicePdf"/> rather than invented.
///
/// It still travels the ordinary document path: <see cref="BuildAsync"/>
/// returns a <see cref="DocumentPdf.Data"/> whose Renderer is this page, so
/// Print, Download, Save to store, the signed share link and the Document
/// Store all work for "expense-sheet" exactly as for every other kind, and
/// nothing in DocumentsController or DocumentArchive had to learn about it.
/// </summary>
public static class ExpenseSheetPdf
{
    /* The sales invoice's palette, value for value (InvoicePdf.cs), so the two
       documents the business prints most look like they came from the same
       office. */
    private const string Navy = "#0B2545";
    private const string NavySoft = "#1B3A66";
    private const string Yellow = "#EDC705";
    private const string Ink = "#111827";
    private const string Muted = "#5B6B80";
    private const string Faint = "#93A1B5";
    private const string Hair = "#E6EBF1";
    private const string ZebraFill = "#F6F8FB";
    private const string PanelFill = "#EDF2F9";
    private const string Success = "#047857";
    private const string Danger = "#B91C1C";
    private const string White = "#FFFFFF";
    private const string OnNavy = "#C3D3E9";
    private const string WarnFill = "#FEF7D6";
    private const string WarnInk = "#7A5B00";
    private const string DangerFill = "#FDECEC";

    private const double Left = 42;
    private const double Right = 553.28;
    private const double Usable = Right - Left;

    /* Nothing is drawn below this line except the footer. */
    private const double Floor = 74;

    /* Column edges for the line table: left edges for text, the right edge for
       the amount. Measured once against a 20-line sheet of real heads and
       vendors rather than weighted, because the amount column must never be
       the one that gets squeezed. */
    private const double ColNo = 53;         // centred
    private const double ColHead = 66;
    private const double ColDesc = 180;
    private const double ColVendor = 318;
    private const double ColPaid = 410;
    private const double ColAmount = Right - 6;   // right

    public sealed record Line(
        int No, string Head, string HeadCode, string? Description, string Vendor,
        string PaidFrom, string Method, decimal Amount,
        /* A pre-sheet line that was reversed on its own: printed for the record
           and left out of every total. */
        bool Excluded);

    public sealed record Model(
        DocumentPdf.LetterHead Company,
        string SheetNo, DateOnly SheetDate, string Location,
        string StatusKey, string? EntryLabel, string? ReversalEntryNo,
        string PreparedBy, DateTime CreatedAt,
        string? ApprovedBy, DateTime? ApprovedAt,
        string? ReversedBy, DateTime? ReversedAt, string? ReversalReason,
        string? Notes,
        IReadOnlyList<Line> Lines);

    /// <summary>What the sheet is called on paper, by its status.</summary>
    public static string StatusWord(string statusKey) => statusKey switch
    {
        "DRAFT" => "Draft",
        "POSTED" => "Approved",
        "REVERSED" => "Reversed",
        _ => statusKey
    };

    // ══════════════════════════════════════════════════════════════════
    //  READ
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads one sheet and hands it to the document path. Null when there is no
    /// such sheet, which the documents controller turns into a 404.
    /// </summary>
    public static async Task<DocumentPdf.Data?> BuildAsync(AppDbContext db, int id)
    {
        var s = await db.ExpenseSheets.AsNoTracking()
            .Where(x => x.SheetId == id)
            .Select(x => new
            {
                x.SheetNo, x.SheetDate, x.Notes, x.CreatedAt, x.ApprovedAt, x.ReversedAt, x.ReversalReason,
                location = x.Location.LocationName,
                status = x.Status.StatusKey,
                entryNo = x.Entry != null ? x.Entry.EntryNo : null,
                reversalNo = x.Entry != null && x.Entry.ReversedByEntry != null ? x.Entry.ReversedByEntry.EntryNo : null,
                createdBy = x.CreatedByUser.FullName,
                approvedBy = x.ApprovedByUser != null ? x.ApprovedByUser.FullName : null,
                reversedBy = x.ReversedByUser != null ? x.ReversedByUser.FullName : null,
                lines = x.Lines.OrderBy(l => l.ExpenseId).Select(l => new
                {
                    head = l.ExpenseAccount.AccountName,
                    code = l.ExpenseAccount.AccountCode,
                    l.Description, l.VendorName, l.Amount,
                    paidFrom = l.PaidFromAccount.AccountName,
                    method = l.Method.MethodName,
                    status = l.Status.StatusKey,
                    entryNo = l.Entry != null ? l.Entry.EntryNo : null
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (s is null) return null;
        var company = await DocumentBuilder.LetterHead(db);

        /* A sheet migration 35 made from expenses posted one by one has no
           single entry of its own; name the per-line entries instead. */
        var entryLabel = s.entryNo;
        if (entryLabel is null)
        {
            /* Only lines still standing: a pre-sheet line that was reversed
               on its own points at an entry that no longer counts. */
            var own = s.lines.Where(l => l.entryNo != null && l.status == "POSTED")
                .Select(l => l.entryNo!).Distinct().ToList();
            entryLabel = own.Count switch
            {
                0 => null,
                1 => own[0],
                _ => $"{own.Count} entries"
            };
        }

        var n = 0;
        var model = new Model(
            company, s.SheetNo, s.SheetDate, s.location, s.status, entryLabel, s.reversalNo,
            s.createdBy, s.CreatedAt, s.approvedBy, s.ApprovedAt,
            s.reversedBy, s.ReversedAt, s.ReversalReason, s.Notes,
            s.lines.Select(l => new Line(
                ++n, l.head, l.code, l.Description, l.VendorName, l.paidFrom, l.method, l.Amount,
                Excluded: s.status != "REVERSED" && l.status is "REVERSED" or "REJECTED" or "CANCELLED"))
                .ToList());

        var total = model.Lines.Where(l => !l.Excluded).Sum(l => l.Amount);

        /* Only the fields the document path itself reads are filled with care --
           the title, number and status for the file name and the Document
           Store listing. The page is drawn by Render below. */
        return new DocumentPdf.Data(
            Company: company,
            Title: "Daily Expense Sheet",
            DocNo: s.SheetNo,
            StatusName: StatusWord(s.status),
            Counterparty: null,
            Meta: new[] { new DocumentPdf.Fact("Date", DocumentPdf.Day(s.SheetDate)) },
            Columns: Array.Empty<DocumentPdf.Col>(),
            Rows: Array.Empty<DocumentPdf.Row>(),
            Totals: new[] { new DocumentPdf.Total("Total", DocumentPdf.Money(total, company.CurrencySymbol), true) },
            Notes: s.Notes,
            Footnote: null,
            PreparedBy: s.createdBy,
            Renderer: () => Render(model));
    }

    // ══════════════════════════════════════════════════════════════════
    //  DRAW
    // ══════════════════════════════════════════════════════════════════

    public static byte[] Render(Model m)
    {
        var pdf = new PdfCanvas();
        var cur = m.Company.CurrencySymbol;
        var live = m.Lines.Where(l => !l.Excluded).ToList();
        var total = live.Sum(l => l.Amount);

        var y = DrawFirstHead(pdf, m, live.Count, total);

        /* ── the lines ── rows are drawn until the page runs out, then the
           table carries on under a short continuation head. The page count is
           not known until the end, so footers are stamped last (trap 13). */
        y = DrawTableHead(pdf, y);
        var zebra = false;
        foreach (var line in m.Lines)
        {
            const double rowHeight = 25;
            if (y - rowHeight < Floor)
            {
                pdf.NewPage();
                y = DrawContinuationHead(pdf, m);
                y = DrawTableHead(pdf, y);
            }
            y = DrawRow(pdf, line, y, rowHeight, zebra);
            zebra = !zebra;
        }

        if (m.Lines.Count == 0)
        {
            pdf.Rect(Left, y - 40, Usable, 40, ZebraFill);
            pdf.TextCenter(Left + Usable / 2, y - 24, "No expenses have been entered on this sheet yet.", 9, Faint);
            y -= 40;
        }

        /* Sub-total rule right under the table, so the eye lands on the number
           before the breakdown starts. */
        y -= 16;
        pdf.Text(ColHead, y, $"{live.Count} {(live.Count == 1 ? "expense" : "expenses")}", 8.4, Muted, bold: true);
        pdf.TextRight(ColAmount, y, Money(total), 9.4, Ink, bold: true);
        y -= 8;
        pdf.Line(ColPaid, y, Right, y, Navy, 1.1);

        /* ── the breakdown by paid-from account, and the grand total ── */
        var byAccount = live
            .GroupBy(l => l.PaidFrom)
            .Select(g => (Account: g.Key, Count: g.Count(), Amount: g.Sum(x => x.Amount)))
            .OrderByDescending(g => g.Amount)
            .ToList();
        var byHead = live
            .GroupBy(l => l.Head)
            .Select(g => (Head: g.Key, Count: g.Count(), Amount: g.Sum(x => x.Amount)))
            .OrderByDescending(g => g.Amount)
            .ToList();

        var summaryHeight = Math.Max(34 + byAccount.Count * 26, 112) + 44;
        if (y - 22 - summaryHeight < Floor)
        {
            pdf.NewPage();
            y = DrawContinuationHead(pdf, m);
        }
        y = DrawSummary(pdf, m, byAccount, total, cur, y - 22);

        /* ── by head, when there is more than one, so the accountant can see at
           a glance where the day's money went. It repeats what the table
           already says, so it is the one block that gives way: when it would
           push the signatures onto a page of their own, it is left out rather
           than printing a second sheet of paper holding two names. ── */
        const double signHeight = 66;
        if (byHead.Count > 1)
        {
            var headHeight = 26 + (int)Math.Ceiling(byHead.Count / 3.0) * 13;
            var bothFit = y - 16 - headHeight - 20 - signHeight >= Floor;
            var signaturesFit = y - 20 - signHeight >= Floor;
            if (bothFit || !signaturesFit)
            {
                if (y - 16 - headHeight < Floor)
                {
                    pdf.NewPage();
                    y = DrawContinuationHead(pdf, m);
                }
                y = DrawByHead(pdf, byHead, total, y - 16);
            }
        }

        /* ── signatures ── */
        if (y - 20 - signHeight < Floor)
        {
            pdf.NewPage();
            y = DrawContinuationHead(pdf, m);
        }
        DrawSignatures(pdf, m, y - 20);

        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            DrawFoot(pdf, m, i + 1, pdf.PageCount);
        }
        return pdf.Build();
    }

    /* ─────────────────────────── page heads ─────────────────────────── */

    private static double DrawFirstHead(PdfCanvas pdf, Model m, int lineCount, decimal total)
    {
        const double bandHeight = 78;
        var bandBottom = PdfCanvas.A4Height - bandHeight;
        var c = m.Company;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 5, PdfCanvas.A4Width, 5, Yellow);

        pdf.Rect(Left, bandBottom + 26, 30, 30, Yellow);
        pdf.TextCenter(Left + 15, bandBottom + 35,
            c.Name.Length > 0 ? c.Name[..1].ToUpperInvariant() : "V", 18, Navy, bold: true);
        pdf.Text(Left + 40, bandBottom + 44, c.Name, 16, White, bold: true);
        pdf.Text(Left + 40, bandBottom + 31, c.LegalName, 8.5, OnNavy);

        pdf.TextRight(Right, bandBottom + 46, "DAILY EXPENSE SHEET", 13, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 31, m.SheetNo, 11.5, White, bold: true);
        pdf.TextRight(Right, bandBottom + 18, StatusWord(m.StatusKey).ToUpperInvariant(), 7.5,
            m.StatusKey == "POSTED" ? Yellow : OnNavy, bold: true);

        /* ── the day, big, on the left: it is the first thing anybody filing
           these looks for ── */
        var y = bandBottom - 26;
        pdf.Text(Left, y, "EXPENSES FOR", 7, Faint, bold: true);
        y -= 20;
        pdf.Text(Left, y, m.SheetDate.ToString("dddd, d MMMM yyyy", En), 16, Navy, bold: true);
        y -= 15;
        pdf.Text(Left, y, m.Location, 10, Ink, bold: true);
        y -= 13;
        foreach (var row in CompanyLines(c).Take(2))
        {
            pdf.Text(Left, y, row, 7.8, Muted);
            y -= 10;
        }

        /* ── the facts panel, right ── */
        var meta = new List<(string, string)>
        {
            ("Sheet No", m.SheetNo),
            ("Date", DocumentPdf.Day(m.SheetDate)),
            ("Location", m.Location),
            ("Status", StatusWord(m.StatusKey)),
        };
        if (!string.IsNullOrWhiteSpace(m.EntryLabel)) meta.Add(("Journal Entry", m.EntryLabel!));
        if (!string.IsNullOrWhiteSpace(m.ReversalEntryNo)) meta.Add(("Reversed By", m.ReversalEntryNo!));

        const double panelLeft = 348;
        var panelTop = bandBottom - 18;
        var panelHeight = 16 + meta.Count * 13;
        pdf.Rect(panelLeft, panelTop - panelHeight, Right - panelLeft, panelHeight, PanelFill);
        pdf.Rect(panelLeft, panelTop - panelHeight, 2.5, panelHeight, Yellow);
        var my = panelTop - 17;
        foreach (var (label, value) in meta)
        {
            pdf.Text(panelLeft + 12, my, label, 7.8, Muted);
            pdf.TextRight(Right - 10, my,
                pdf.Ellipsis(value, 8.4, Right - panelLeft - 34 - PdfCanvas.Width(label, 7.8), true), 8.4, Ink, bold: true);
            my -= 13;
        }

        y = Math.Min(y, panelTop - panelHeight) - 14;

        /* ── three figures across the page ── */
        const double tileHeight = 42;
        const double gap = 10;
        var tileWidth = (Usable - gap * 2) / 3;
        var paidFromCount = m.Lines.Where(l => !l.Excluded).Select(l => l.PaidFrom).Distinct().Count();
        var tiles = new[]
        {
            ("TOTAL SPENT", $"{m.Company.CurrencySymbol} {Money(total)}", true),
            ("EXPENSES", lineCount.ToString("N0", En), false),
            ("PAID FROM", paidFromCount == 1 ? "1 account" : $"{paidFromCount} accounts", false),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var x = Left + i * (tileWidth + gap);
            var (label, value, strong) = tiles[i];
            pdf.Rect(x, y - tileHeight, tileWidth, tileHeight, strong ? Navy : ZebraFill);
            pdf.Rect(x, y - tileHeight, 3, tileHeight, Yellow);
            pdf.Text(x + 13, y - 16, label, 7, strong ? Yellow : Faint, bold: true);
            pdf.Text(x + 13, y - 32, value, strong ? 14 : 13, strong ? White : Navy, bold: true);
        }
        y -= tileHeight + 12;

        /* ── what state the paper is in, when it is not simply approved ── */
        if (m.StatusKey == "DRAFT")
        {
            y = Ribbon(pdf, y, WarnFill, Yellow, WarnInk,
                "DRAFT -- NOT YET APPROVED",
                "Nothing on this sheet has reached the ledger. It is posted when the accountant approves the day.");
        }
        else if (m.StatusKey == "REVERSED")
        {
            var when = m.ReversedAt is null ? "" : $" on {DocumentPdf.Day(m.ReversedAt)}";
            var who = string.IsNullOrWhiteSpace(m.ReversedBy) ? "" : $" by {m.ReversedBy}";
            var entry = string.IsNullOrWhiteSpace(m.ReversalEntryNo) ? "" : $" Reversal entry {m.ReversalEntryNo}.";
            y = Ribbon(pdf, y, DangerFill, Danger, Danger,
                $"REVERSED{when}{who}".ToUpperInvariant(),
                $"{m.ReversalReason ?? "No reason recorded."}{entry}");
        }

        return y - 4;
    }

    private static double Ribbon(PdfCanvas pdf, double y, string fill, string bar, string ink, string title, string body)
    {
        const double height = 34;
        pdf.Rect(Left, y - height, Usable, height, fill);
        pdf.Rect(Left, y - height, 3, height, bar);
        pdf.Text(Left + 13, y - 14, title, 8, ink, bold: true);
        pdf.Text(Left + 13, y - 26, pdf.Ellipsis(body, 7.8, Usable - 26), 7.8, Muted);
        return y - height - 10;
    }

    private static double DrawContinuationHead(PdfCanvas pdf, Model m)
    {
        const double bandHeight = 44;
        var bandBottom = PdfCanvas.A4Height - bandHeight;
        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom - 4, PdfCanvas.A4Width, 4, Yellow);
        pdf.Text(Left, bandBottom + 17, m.Company.Name, 11, White, bold: true);
        pdf.TextRight(Right, bandBottom + 24, $"Daily Expense Sheet {m.SheetNo}  (continued)", 9.5, Yellow, bold: true);
        pdf.TextRight(Right, bandBottom + 11, $"{DocumentPdf.Day(m.SheetDate)}  ·  {m.Location}", 7.5, OnNavy);
        return bandBottom - 24;
    }

    /* ─────────────────────────── the table ─────────────────────────── */

    private static double DrawTableHead(PdfCanvas pdf, double y)
    {
        const double headHeight = 21;
        pdf.Rect(Left, y - headHeight, Usable, headHeight, NavySoft);
        var ty = y - headHeight + 7;
        pdf.TextCenter(ColNo, ty, "#", 7.5, White, bold: true);
        pdf.Text(ColHead, ty, "EXPENSE HEAD", 7.5, White, bold: true);
        pdf.Text(ColDesc, ty, "DESCRIPTION", 7.5, White, bold: true);
        pdf.Text(ColVendor, ty, "VENDOR", 7.5, White, bold: true);
        pdf.Text(ColPaid, ty, "PAID FROM", 7.5, White, bold: true);
        pdf.TextRight(ColAmount, ty, "AMOUNT", 7.5, White, bold: true);
        return y - headHeight;
    }

    private static double DrawRow(PdfCanvas pdf, Line l, double y, double height, bool zebra)
    {
        var bottom = y - height;
        if (zebra) pdf.Rect(Left, bottom, Usable, height, ZebraFill);

        var ink = l.Excluded ? Faint : Ink;
        var top = bottom + 14;
        var sub = bottom + 5;

        pdf.TextCenter(ColNo, bottom + 10, l.No.ToString(En), 8, Faint);

        pdf.Text(ColHead, top, pdf.Ellipsis(l.Head, 8.4, ColDesc - ColHead - 8, true), 8.4, ink, bold: true);
        pdf.Text(ColHead, sub, l.HeadCode, 6.8, Faint);

        var desc = string.IsNullOrWhiteSpace(l.Description) ? "-" : l.Description!.Replace('\n', ' ');
        pdf.Text(ColDesc, bottom + 10, pdf.Ellipsis(desc, 8.2, ColVendor - ColDesc - 8), 8.2, ink);

        pdf.Text(ColVendor, bottom + 10, pdf.Ellipsis(l.Vendor, 8.2, ColPaid - ColVendor - 8), 8.2, ink);

        pdf.Text(ColPaid, top, pdf.Ellipsis(l.PaidFrom, 8, ColAmount - 58 - ColPaid), 8, ink);
        pdf.Text(ColPaid, sub, l.Excluded ? "reversed on its own" : l.Method, 6.8, l.Excluded ? Danger : Faint);

        var amount = Money(l.Amount);
        pdf.TextRight(ColAmount, bottom + 10, amount, 8.8, ink, bold: !l.Excluded);
        if (l.Excluded)
        {
            /* Struck through: it happened, it was undone, it is not in the total. */
            var w = PdfCanvas.Width(amount, 8.8);
            pdf.Line(ColAmount - w - 1, bottom + 13, ColAmount + 1, bottom + 13, Danger, 0.8);
        }

        pdf.Line(Left, bottom, Right, bottom, Hair, 0.5);
        return bottom;
    }

    /* ─────────────────────────── the summary ─────────────────────────── */

    private static double DrawSummary(PdfCanvas pdf, Model m,
        List<(string Account, int Count, decimal Amount)> byAccount, decimal total, string cur, double y)
    {
        /* LEFT: what each drawer or bank account paid out -- the figure the
           cashier counts the drawer against at the end of the day. */
        const double leftWidth = 290;
        var rows = Math.Max(byAccount.Count, 1);
        var panelHeight = 34 + rows * 26;
        pdf.Rect(Left, y - panelHeight, leftWidth, panelHeight, ZebraFill);
        pdf.Rect(Left, y - panelHeight, 3, panelHeight, Yellow);
        pdf.Text(Left + 13, y - 16, "TOTALS BY PAID-FROM ACCOUNT", 7, Faint, bold: true);

        var ry = y - 34;
        if (byAccount.Count == 0)
            pdf.Text(Left + 13, ry - 6, "Nothing paid out yet.", 8.4, Muted);

        foreach (var a in byAccount)
        {
            pdf.Text(Left + 13, ry, pdf.Ellipsis(a.Account, 8.6, 170, true), 8.6, Ink, bold: true);
            pdf.Text(Left + 13 + Math.Min(PdfCanvas.Width(a.Account, 8.6, true), 170) + 6, ry,
                $"{a.Count} {(a.Count == 1 ? "line" : "lines")}", 7.2, Faint);
            pdf.TextRight(Left + leftWidth - 12, ry, Money(a.Amount), 8.8, Ink, bold: true);

            /* A thin bar showing its share of the day. */
            var barWidth = leftWidth - 25;
            var share = total > 0 ? (double)(a.Amount / total) : 0;
            pdf.Rect(Left + 13, ry - 9, barWidth, 3, Hair);
            if (share > 0) pdf.Rect(Left + 13, ry - 9, Math.Max(2, barWidth * share), 3, NavySoft);
            ry -= 26;
        }

        /* RIGHT: the totals box and the band. */
        const double boxLeft = 348;
        var line = y - 16;
        pdf.Rect(boxLeft, y - 58, Right - boxLeft, 58, PanelFill);
        void Pair(string label, string value, string colour)
        {
            pdf.Text(boxLeft + 10, line, label, 8.2, Muted);
            pdf.TextRight(Right - 10, line, value, 8.6, colour);
            line -= 14;
        }
        var excluded = m.Lines.Where(l => l.Excluded).ToList();
        Pair("Expenses on this sheet", m.Lines.Count(l => !l.Excluded).ToString("N0", En), Ink);
        Pair("Paid from", byAccount.Count == 1 ? "1 account" : $"{byAccount.Count} accounts", Ink);
        if (excluded.Count > 0)
            Pair($"Left out ({excluded.Count} reversed)", $"({Money(excluded.Sum(l => l.Amount))})", Danger);
        else
            Pair("Journal entry", m.EntryLabel ?? (m.StatusKey == "DRAFT" ? "On approval" : "-"),
                m.EntryLabel is null ? Faint : Ink);

        const double bandHeight = 28;
        var bandTop = y - 64;
        pdf.Rect(boxLeft, bandTop - bandHeight, Right - boxLeft, bandHeight, Navy);
        pdf.Text(boxLeft + 10, bandTop - 18, "GRAND TOTAL", 9.5, Yellow, bold: true);
        pdf.TextRight(Right - 10, bandTop - 19, $"{cur} {Money(total)}", 13, White, bold: true);

        /* In words, under the band: the figure a cheque or a cash book is
           checked against. */
        var wy = pdf.TextWrapped(boxLeft, bandTop - bandHeight - 13, InvoicePdf.Words(total), 7.6,
            Right - boxLeft, Muted);

        var bottom = Math.Min(y - panelHeight, wy);

        if (!string.IsNullOrWhiteSpace(m.Notes))
        {
            var ny = pdf.TextWrapped(Left, bottom - 14, "NOTE", 7, Usable, Faint, bold: true);
            bottom = pdf.TextWrapped(Left, ny - 1, m.Notes!.Replace('\n', ' '), 8, Usable, Muted) + 4;
        }
        return bottom;
    }

    private static double DrawByHead(PdfCanvas pdf,
        List<(string Head, int Count, decimal Amount)> byHead, decimal total, double y)
    {
        pdf.Text(Left, y - 4, "WHERE THE MONEY WENT", 7, Faint, bold: true);
        y -= 12;
        pdf.Line(Left, y, Right, y, Hair, 0.7);

        const double gutter = 18;
        const double colWidth = (Right - Left - gutter * 2) / 3;
        var rowY = y - 12;
        for (var i = 0; i < byHead.Count; i++)
        {
            var col = i % 3;
            var x = Left + col * (colWidth + gutter);
            var h = byHead[i];
            var pct = total > 0 ? h.Amount / total * 100 : 0;
            pdf.Text(x, rowY, pdf.Ellipsis(h.Head, 7.8, colWidth - 92), 7.8, Ink);
            pdf.TextRight(x + colWidth - 52, rowY, pct is > 0 and < 0.5m ? "<1%" : $"{pct:0}%", 7, Faint);
            pdf.TextRight(x + colWidth, rowY, Money(h.Amount), 8, Ink, bold: true);
            if (col == 2 || i == byHead.Count - 1) rowY -= 13;
        }
        return rowY + 4;
    }

    /* ─────────────────────────── signatures ─────────────────────────── */

    private static void DrawSignatures(PdfCanvas pdf, Model m, double y)
    {
        const double gap = 14;
        var width = (Usable - gap * 2) / 3;

        var boxes = new[]
        {
            ("PREPARED BY", m.PreparedBy, DocumentPdf.Day(m.CreatedAt), true),
            ("APPROVED BY",
                m.ApprovedBy ?? (m.StatusKey == "DRAFT" ? "Awaiting approval" : "-"),
                m.ApprovedAt is null ? "" : DocumentPdf.Day(m.ApprovedAt),
                m.ApprovedBy is not null),
            ("RECEIVED / CHECKED BY", "", "", false),
        };

        for (var i = 0; i < boxes.Length; i++)
        {
            var x = Left + i * (width + gap);
            var (label, name, when, known) = boxes[i];
            pdf.Text(x, y - 4, label, 7, Faint, bold: true);
            /* The line to sign on. */
            pdf.Line(x, y - 36, x + width, y - 36, Muted, 0.7);
            if (!string.IsNullOrWhiteSpace(name))
                pdf.Text(x, y - 48, pdf.Ellipsis(name, 8.6, width, known), 8.6, known ? Ink : Faint, bold: known);
            if (!string.IsNullOrWhiteSpace(when))
                pdf.Text(x, y - 59, when, 7.4, Muted);
            if (i == 2)
                pdf.Text(x, y - 48, "Name, signature and date", 7.4, Faint);
        }
    }

    /* ───────────────────────────── footer ───────────────────────────── */

    private static void DrawFoot(PdfCanvas pdf, Model m, int pageNo, int pageCount)
    {
        const double y = 62;
        var c = m.Company;
        pdf.Line(Left, y, Right, y, Hair, 0.7);
        pdf.Text(Left, y - 14,
            "Attach every original receipt to the office copy of this sheet, in the order the lines are numbered.",
            7.2, Muted);
        pdf.Text(Left, y - 24,
            $"{c.LegalName}  ·  NTN {c.Ntn}  ·  STRN {c.Strn}  ·  {c.Phone}  ·  {c.Email}", 7.2, Faint);
        pdf.Text(Left, y - 38,
            $"{m.SheetNo}  ·  {DocumentPdf.Day(m.SheetDate)}  ·  {m.Location}.  Computer-generated document.",
            7, Faint);
        pdf.TextRight(Right, y - 38, $"Page {pageNo} of {pageCount}", 7, Faint);
        pdf.Rect(0, 0, PdfCanvas.A4Width, 5, Yellow);
    }

    /* ──────────────────────────── helpers ──────────────────────────── */

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static string Money(decimal v) => v.ToString("N2", En);

    private static IEnumerable<string> CompanyLines(DocumentPdf.LetterHead c)
    {
        var place = string.Join(", ", new[] { c.Address, c.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (place.Length > 0) yield return place;
        var contact = string.Join("   ", new[] { c.Phone, c.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (contact.Length > 0) yield return contact;
    }
}
