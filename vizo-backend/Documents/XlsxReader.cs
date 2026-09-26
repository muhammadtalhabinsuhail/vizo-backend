using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace vizo_backend.Documents;

/// <summary>
/// Reads the first sheet of an .xlsx into rows of strings. The other half of
/// <see cref="XlsxWriter"/>, and written the same way: no dependency.
///
/// WHY NOT A PACKAGE. The customer import (CustomerLedgerController) needs to
/// read ONE simple sheet the owner fills in by hand -- a code, a name, a city, a
/// few numbers. ClosedXML or EPPlus would do it in a line, and would be a new
/// NuGet reference in vizo-backend.csproj, which is Talha's file and must not be
/// touched from this branch. An .xlsx is a zip of XML: the sheet is
/// xl/worksheets/sheet1.xml, text cells point into xl/sharedStrings.xml, and
/// that is all an import of typed-in values ever needs.
///
/// What it deliberately does NOT do: formulas are read as their cached value
/// (what Excel last displayed), dates arrive as Excel serial numbers, and
/// formatting is ignored. Every cell comes back as a trimmed string; the caller
/// decides what a column means.
/// </summary>
public static class XlsxReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Every row of the first worksheet, top to bottom, each padded to the
    /// widest row so a blank cell in the middle keeps its column. Throws
    /// InvalidDataException when the bytes are not an .xlsx.
    /// </summary>
    public static List<string[]> ReadFirstSheet(Stream xlsx)
    {
        using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read, leaveOpen: true);

        var shared = new List<string>();
        var sst = zip.GetEntry("xl/sharedStrings.xml");
        if (sst is not null)
        {
            using var s = sst.Open();
            var doc = XDocument.Load(s);
            foreach (var si in doc.Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
        }

        var sheetPath = FirstSheetPath(zip) ?? "xl/worksheets/sheet1.xml";
        var sheet = zip.GetEntry(sheetPath)
                    ?? throw new InvalidDataException("The workbook has no worksheet.");

        var rows = new List<Dictionary<int, string>>();
        using (var s = sheet.Open())
        {
            var doc = XDocument.Load(s);
            var data = doc.Root!.Element(Main + "sheetData");
            if (data is null) return new List<string[]>();

            foreach (var row in data.Elements(Main + "row"))
            {
                var cells = new Dictionary<int, string>();
                var next = 0;
                foreach (var c in row.Elements(Main + "c"))
                {
                    var col = c.Attribute("r") is { } r ? ColumnIndex(r.Value) : next;
                    next = col + 1;
                    cells[col] = CellText(c, shared).Trim();
                }
                rows.Add(cells);
            }
        }

        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count == 0 ? 0 : r.Keys.Max() + 1);
        return rows.Select(r =>
        {
            var arr = new string[width];
            for (var i = 0; i < width; i++) arr[i] = r.TryGetValue(i, out var v) ? v : "";
            return arr;
        }).ToList();
    }

    /// <summary>The path of the first sheet listed in the workbook, which is not always sheet1.xml.</summary>
    private static string? FirstSheetPath(ZipArchive zip)
    {
        var wb = zip.GetEntry("xl/workbook.xml");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wb is null || rels is null) return null;

        string? rid;
        using (var s = wb.Open())
            rid = XDocument.Load(s).Root?.Element(Main + "sheets")?.Elements(Main + "sheet")
                .FirstOrDefault()?.Attribute(Rel + "id")?.Value;
        if (rid is null) return null;

        using (var s = rels.Open())
        {
            var target = XDocument.Load(s).Root?.Elements(PkgRel + "Relationship")
                .FirstOrDefault(r => r.Attribute("Id")?.Value == rid)?.Attribute("Target")?.Value;
            if (target is null) return null;
            return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }
    }

    private static string CellText(XElement c, List<string> shared)
    {
        var type = c.Attribute("t")?.Value;
        if (type == "inlineStr")
            return string.Concat(c.Descendants(Main + "t").Select(t => t.Value));

        var v = c.Element(Main + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i >= 0 && i < shared.Count ? shared[i] : "";
        if (type == "b") return v == "1" ? "TRUE" : "FALSE";
        return v;
    }

    /// <summary>"B7" -> 1, "AA3" -> 26.</summary>
    private static int ColumnIndex(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return Math.Max(0, n - 1);
    }
}
