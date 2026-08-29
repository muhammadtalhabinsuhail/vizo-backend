using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace vizo_backend.Documents;

/// <summary>
/// A very small .xlsx writer -- enough for "export this list" and nothing more.
///
/// WHY THERE IS NO LIBRARY HERE, again. EPPlus went commercial at version 5 and
/// wants a licence key set at startup; ClosedXML is MIT but drags in
/// DocumentFormat.OpenXml and its whole object model for what is, here, one
/// sheet of one table. An .xlsx is a zip of half a dozen small XML parts, and
/// System.IO.Compression is in the framework. Same reasoning as PdfCanvas.
///
/// Deliberately narrow: one sheet, one header row, typed cells, frozen header,
/// auto-filter, sensible column widths. No formulas, no charts, no merged
/// cells. If any of those are ever wanted, that is the moment to reach for a
/// library rather than to grow this file.
///
/// Strings are written INLINE rather than through a shared-strings table. That
/// costs a few kilobytes on a big export and saves a whole part, a whole set of
/// indices, and the class of bug where the indices drift out of step with the
/// cells that point at them.
/// </summary>
public static class XlsxWriter
{
    public enum CellKind { Text, Number, Money, Integer, Date, Percent }

    /// <param name="Field">The JSON property to read for this column.</param>
    /// <param name="Width">Column width in characters. 0 lets the writer guess from the header.</param>
    public sealed record Column(string Header, string Field, CellKind Kind = CellKind.Text, double Width = 0);

    private sealed record Cell(string? Text, double? Number, CellKind Kind);

    /* Style indices, in the order they are written into styles.xml below. */
    private const int StyleDefault = 0;
    private const int StyleHeader = 1;
    private const int StyleMoney = 2;
    private const int StyleInteger = 3;
    private const int StyleDate = 4;
    private const int StylePercent = 5;

    /// <summary>
    /// Builds a workbook from a JSON array -- the `items` of a list endpoint --
    /// so an export is always the same data the screen was showing.
    /// </summary>
    public static byte[] FromJson(string sheetName, JsonElement rows, IReadOnlyList<Column> columns)
    {
        var table = new List<List<Cell>>();

        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                var cells = new List<Cell>(columns.Count);
                foreach (var col in columns)
                {
                    row.TryGetProperty(col.Field, out var v);
                    cells.Add(ToCell(v, col.Kind));
                }
                table.Add(cells);
            }
        }

        return Build(sheetName, columns, table);
    }

    /// <summary>
    /// Same, but takes a whole list-endpoint payload. Some list actions return a
    /// bare array and some return { total, page, pageSize, items } -- the caller
    /// should not have to know which.
    /// </summary>
    public static byte[] FromPayload(string sheetName, JsonElement payload, IReadOnlyList<Column> columns)
    {
        var rows = payload.ValueKind == JsonValueKind.Array
            ? payload
            : payload.TryGetProperty("items", out var items) ? items : default;

        return FromJson(sheetName, rows, columns);
    }

    private static Cell ToCell(JsonElement v, CellKind kind)
    {
        if (v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new Cell(null, null, kind);

        switch (kind)
        {
            case CellKind.Number:
            case CellKind.Money:
            case CellKind.Integer:
            case CellKind.Percent:
                return v.ValueKind == JsonValueKind.Number
                    ? new Cell(null, v.GetDouble(), kind)
                    /* A number column that arrives as a string is a data
                       problem, not a formatting one -- show it rather than
                       silently writing a blank cell. */
                    : new Cell(Flatten(v), null, CellKind.Text);

            case CellKind.Date:
                /* Excel stores a date as days since 1900-01-00, and a real date
                   cell is what lets somebody sort or filter by month. A string
                   that merely looks like a date does not. */
                return DateTime.TryParse(Flatten(v), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var d)
                    ? new Cell(null, ToSerial(d), CellKind.Date)
                    : new Cell(Flatten(v), null, CellKind.Text);

            default:
                return new Cell(Flatten(v), null, CellKind.Text);
        }
    }

    private static string Flatten(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Undefined or JsonValueKind.Null => "",
        _ => v.ToString()
    };

    /// <summary>
    /// Excel's day zero is 1899-12-30 -- not 1900-01-01, because Lotus 1-2-3
    /// believed 1900 was a leap year and Excel kept the bug for compatibility.
    /// </summary>
    private static double ToSerial(DateTime d) => (d.Date - new DateTime(1899, 12, 30)).TotalDays;

    /* ─────────────────────────── the zip ─────────────────────────── */

    private static byte[] Build(string sheetName, IReadOnlyList<Column> columns, List<List<Cell>> rows)
    {
        var safeSheet = SheetName(sheetName);
        var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, "[Content_Types].xml", ContentTypes());
            Put(zip, "_rels/.rels", RootRels());
            Put(zip, "xl/workbook.xml", Workbook(safeSheet));
            Put(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
            Put(zip, "xl/styles.xml", Styles());
            Put(zip, "xl/worksheets/sheet1.xml", Sheet(columns, rows));
        }

        return buffer.ToArray();
    }

    private static void Put(ZipArchive zip, string path, string xml)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private static string ContentTypes() =>
        Decl +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
        "</Types>";

    private static string RootRels() =>
        Decl +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string Workbook(string sheetName) =>
        Decl +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        $"<sheets><sheet name=\"{Esc(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
        "</workbook>";

    private static string WorkbookRels() =>
        Decl +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    /// <summary>
    /// Six cell formats, in the order the Style* constants name them. The
    /// numFmtId values above 163 are custom; the ones below are Excel's own
    /// built-ins and must not be redefined.
    /// </summary>
    private static string Styles() =>
        Decl +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"4\">" +
        "<numFmt numFmtId=\"164\" formatCode=\"#,##0.00\"/>" +
        "<numFmt numFmtId=\"165\" formatCode=\"#,##0\"/>" +
        "<numFmt numFmtId=\"166\" formatCode=\"dd\\-mmm\\-yyyy\"/>" +
        "<numFmt numFmtId=\"167\" formatCode=\"0.0%\"/>" +
        "</numFmts>" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
        "</fonts>" +
        "<fills count=\"3\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF031833\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"6\">" +
        "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
        "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
        "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "<xf numFmtId=\"167\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
        "</cellXfs>" +
        "</styleSheet>";

    private static string Sheet(IReadOnlyList<Column> columns, List<List<Cell>> rows)
    {
        var sb = new StringBuilder(Decl);
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        /* Column widths. A guessed width from the header beats Excel's default
           of 8.43 characters, which truncates almost every heading here. */
        sb.Append("<cols>");
        for (var i = 0; i < columns.Count; i++)
        {
            var w = columns[i].Width > 0 ? columns[i].Width : Math.Clamp(columns[i].Header.Length + 4, 12, 40);
            sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{w.ToString("0.##", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        }
        sb.Append("</cols>");

        sb.Append("<sheetData>");

        sb.Append("<row r=\"1\">");
        for (var c = 0; c < columns.Count; c++)
            sb.Append(TextCell(Ref(c, 1), columns[c].Header, StyleHeader));
        sb.Append("</row>");

        for (var r = 0; r < rows.Count; r++)
        {
            var rowNo = r + 2;
            sb.Append($"<row r=\"{rowNo}\">");
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                var reference = Ref(c, rowNo);

                if (cell.Number is { } n)
                {
                    var style = cell.Kind switch
                    {
                        CellKind.Money => StyleMoney,
                        CellKind.Integer => StyleInteger,
                        CellKind.Date => StyleDate,
                        CellKind.Percent => StylePercent,
                        _ => StyleDefault
                    };
                    var v = cell.Kind == CellKind.Percent ? n / 100.0 : n;
                    sb.Append($"<c r=\"{reference}\" s=\"{style}\"><v>{v.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                }
                else if (!string.IsNullOrEmpty(cell.Text))
                {
                    sb.Append(TextCell(reference, cell.Text!, StyleDefault));
                }
            }
            sb.Append("</row>");
        }

        sb.Append("</sheetData>");

        /* Freeze the header and turn on the filter dropdowns -- an export
           nobody can sort is a screenshot with extra steps. */
        var last = Ref(Math.Max(0, columns.Count - 1), Math.Max(1, rows.Count + 1));
        sb.Insert(sb.ToString().IndexOf("<cols>", StringComparison.Ordinal),
            "<sheetViews><sheetView workbookViewId=\"0\">" +
            "<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>" +
            "</sheetView></sheetViews>");
        sb.Append($"<autoFilter ref=\"A1:{last}\"/>");

        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string TextCell(string reference, string text, int style) =>
        $"<c r=\"{reference}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>";

    /// <summary>Zero-based column index and one-based row to an A1 reference.</summary>
    private static string Ref(int col, int row)
    {
        var name = "";
        for (var n = col; ; n = n / 26 - 1)
        {
            name = (char)('A' + n % 26) + name;
            if (n < 26) break;
        }
        return name + row;
    }

    /// <summary>
    /// Sheet names cannot exceed 31 characters or contain : \ / ? * [ ].
    /// Excel refuses to open the file rather than telling you which rule broke.
    /// </summary>
    private static string SheetName(string name)
    {
        var cleaned = new string(name.Where(c => !":\\/?*[]".Contains(c)).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Sheet1";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    /* XML 1.0 forbids most control characters outright, and a
                       stray one makes the whole workbook unreadable. */
                    if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    public const string ContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
