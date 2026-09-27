using System.Text;

namespace vizo_backend.Documents;

/// <summary>
/// Reads a .csv into rows of strings -- the same shape <see cref="XlsxReader"/>
/// returns, so an import can take either file and read its columns once.
///
/// WHY. Bank statements arrive as .csv as often as .xlsx: most Pakistani banks'
/// internet banking offers "Download CSV" and nothing else. A package would be a
/// new reference in vizo-backend.csproj, which is Talha's file.
///
/// What it handles: RFC 4180 quoting (a quoted field may hold commas, doubled
/// quotes and line breaks), CRLF or LF, a UTF-8 byte-order mark, and a
/// semicolon or tab as the separator when the first line has more of those than
/// commas (Excel in some locales saves "CSV" with semicolons). Every cell comes
/// back trimmed; rows are padded to the widest so a blank cell keeps its column.
/// </summary>
public static class CsvReader
{
    public static List<string[]> Read(Stream csv)
    {
        using var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd();

        var firstLine = text.Split('\n', 2)[0];
        var sep = new[] { ',', ';', '\t' }
            .OrderByDescending(c => firstLine.Count(ch => ch == c))
            .First();

        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }

            if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString().Trim()); cell.Clear(); }
            else if (c == '\r') { /* the \n that follows ends the row */ }
            else if (c == '\n')
            {
                row.Add(cell.ToString().Trim()); cell.Clear();
                rows.Add(row); row = new List<string>();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            rows.Add(row);
        }

        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        return rows.Select(r =>
        {
            var arr = new string[width];
            for (var i = 0; i < width; i++) arr[i] = i < r.Count ? r[i] : "";
            return arr;
        }).ToList();
    }
}
