namespace vizo_backend.Documents;

/// <summary>
/// The customer's legal documents, as one PDF: a cover page saying whose they
/// are and what is in the set, then one page per photograph -- CNIC front and
/// back, the shop's business card front and back, and both pages of the
/// affidavit.
///
/// WHY ONE FILE INSTEAD OF SIX LINKS. The owner asked for a single
/// legal_documents.pdf that anybody allowed to see a customer can download.
/// Six Cloudinary links in six places is six things to lose; one document is
/// what gets attached to an email, printed for a file, or handed to somebody
/// asking who this shop is.
///
/// THE PICTURES ARE FETCHED AS JPEG, always. Cloudinary is asked for
/// f_jpg,q_82,w_1600,c_limit on the way in, whatever the phone actually took,
/// because PdfCanvas embeds JPEG and nothing else -- see PdfCanvas.Jpeg.
/// A photograph that cannot be fetched or is not a readable JPEG gets a page
/// saying so rather than being left out silently: a set with five pages where
/// there should be six is a question nobody can answer later.
/// </summary>
public static class LegalDocsPdf
{
    private const double Left = 40;
    private const double Right = PdfCanvas.A4Width - 40;
    private const string Navy = "#0F172A";
    private const string Ink = "#111827";
    private const string Faint = "#6B7280";
    private const string Line = "#E5E7EB";
    private const string Yellow = "#FACC15";

    /// <summary>One photograph in the set.</summary>
    public record Sheet(string Caption, string? Note, byte[]? Jpeg);

    /// <summary>Who the documents belong to, for the cover page.</summary>
    public record Owner(
        string Code, string LegalName, string DisplayName, string? Category,
        string? Phone, string? AltPhone, string? Email, string? Address, string? City,
        string? Cnic, string? Ntn, string? SalesPerson, string? OpenedBy, DateOnly? OpenedOn);

    public static byte[] Build(DocumentPdf.LetterHead company, Owner owner, IReadOnlyList<Sheet> sheets)
    {
        var pdf = new PdfCanvas();

        /* ───────────────────────── cover ───────────────────────── */
        var y = Header(pdf, company, "CUSTOMER LEGAL DOCUMENTS");

        pdf.Text(Left, y, owner.DisplayName, 16, Ink, bold: true);
        y -= 18;
        if (!string.Equals(owner.DisplayName, owner.LegalName, StringComparison.OrdinalIgnoreCase))
        {
            pdf.Text(Left, y, $"Registered name: {owner.LegalName}", 9.5, Faint);
            y -= 14;
        }
        pdf.Text(Left, y, owner.Code + (owner.Category is null ? "" : $"  ·  {owner.Category}"), 9.5, Faint);
        y -= 24;

        pdf.Line(Left, y, Right, y, Line);
        y -= 20;

        y = Fact(pdf, y, "Phone", owner.Phone);
        y = Fact(pdf, y, "Other phone", owner.AltPhone);
        y = Fact(pdf, y, "Email", owner.Email);
        y = Fact(pdf, y, "Address", owner.Address);
        y = Fact(pdf, y, "City", owner.City);
        y = Fact(pdf, y, "CNIC", owner.Cnic);
        y = Fact(pdf, y, "NTN", owner.Ntn);
        y = Fact(pdf, y, "Salesman", owner.SalesPerson);
        y = Fact(pdf, y, "Account opened by", owner.OpenedBy);
        y = Fact(pdf, y, "Opened on", owner.OpenedOn is null ? null : DocumentPdf.Day(owner.OpenedOn.Value));

        y -= 14;
        pdf.Line(Left, y, Right, y, Line);
        y -= 22;

        pdf.Text(Left, y, "WHAT IS IN THIS FILE", 9, Faint, bold: true);
        y -= 16;

        foreach (var sheet in sheets)
        {
            var has = sheet.Jpeg is not null && sheet.Jpeg.Length > 0;
            pdf.Text(Left + 4, y, (has ? "·  " : "-  ") + sheet.Caption, 10, has ? Ink : Faint);
            if (!has) pdf.TextRight(Right, y, sheet.Note ?? "not provided", 9, Faint);
            y -= 15;
        }

        y -= 10;
        pdf.TextWrapped(Left, y,
            "Every page after this one is a photograph taken when the account was opened. " +
            "Nothing has been cropped, corrected or re-typed.",
            8.5, Right - Left, Faint);

        /* ──────────────────── one page per photograph ──────────────────── */
        var pageNo = 1;
        foreach (var sheet in sheets)
        {
            if (sheet.Jpeg is null || sheet.Jpeg.Length == 0) continue;

            pdf.NewPage();
            pageNo++;

            var top = Header(pdf, company, sheet.Caption.ToUpperInvariant());

            /* The picture fills what is left of the page, keeping its shape.
               A document photograph that has been stretched is a document
               somebody will argue about. */
            const double bottom = 70;
            var boxWidth = Right - Left;
            var boxHeight = top - bottom;

            var drawn = Fit(pdf, sheet.Jpeg, Left, bottom, boxWidth, boxHeight);
            if (!drawn)
            {
                pdf.Text(Left, top - 30,
                    "This photograph could not be included: it is not a JPEG this system can embed.",
                    10, "#B91C1C");
                pdf.Text(Left, top - 46, sheet.Note ?? "", 9, Faint);
            }

            if (sheet.Note is not null && drawn)
                pdf.Text(Left, bottom - 16, sheet.Note, 8.5, Faint);
        }

        /* Footers last, when the count is finally known -- trap 13. */
        for (var i = 0; i < pdf.PageCount; i++)
        {
            pdf.SelectPage(i);
            pdf.Line(Left, 52, Right, 52, Line);
            pdf.Text(Left, 38, $"{owner.Code}  ·  {owner.DisplayName}", 8, Faint);
            pdf.TextRight(Right, 38, $"Page {i + 1} of {pdf.PageCount}", 8, Faint);
        }

        return pdf.Build();
    }

    /* ───────────────────────── the bits ───────────────────────── */

    /// <summary>The navy band every page in this project wears, and the title.</summary>
    private static double Header(PdfCanvas pdf, DocumentPdf.LetterHead c, string title)
    {
        const double bandHeight = 58;
        var bandTop = PdfCanvas.A4Height - 36;
        var bandBottom = bandTop - bandHeight;

        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, bandHeight, Navy);
        pdf.Rect(0, bandBottom, PdfCanvas.A4Width, 3, Yellow);

        pdf.Text(Left, bandTop - 24, c.Name, 15, "#FFFFFF", bold: true);
        pdf.Text(Left, bandTop - 40, c.LegalName, 8.5, "#CBD5E1");
        pdf.TextRight(Right, bandTop - 24, title, 11, Yellow, bold: true);

        return bandBottom - 34;
    }

    private static double Fact(PdfCanvas pdf, double y, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return y;
        pdf.Text(Left, y, label, 9, Faint);
        pdf.Text(Left + 130, y, value, 10, Ink);
        return y - 16;
    }

    /// <summary>
    /// Draws the photograph as large as it will go inside the box without
    /// changing its shape, centred.
    /// </summary>
    private static bool Fit(PdfCanvas pdf, byte[] jpeg, double x, double y, double w, double h)
    {
        var size = PdfCanvas.JpegPixels(jpeg);
        if (size is null) return false;

        var scale = Math.Min(w / size.Value.Width, h / size.Value.Height);
        var drawW = size.Value.Width * scale;
        var drawH = size.Value.Height * scale;

        return pdf.Jpeg(x + (w - drawW) / 2, y + (h - drawH) / 2, drawW, drawH, jpeg);
    }
}
