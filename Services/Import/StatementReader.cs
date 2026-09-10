using System.Text;
using ClosedXML.Excel;

namespace ExpenseTracker.Services.Import;

/// <summary>A statement flattened to a header row and the rows under it.</summary>
public record StatementTable(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows)
{
    public static readonly StatementTable Empty = new([], []);

    public string Cell(string[] row, int index) =>
        index < 0 || index >= row.Length ? "" : row[index].Trim();
}

/// <summary>
/// Turns a bank export into a table. Banks agree on nothing: the delimiter may be
/// a semicolon or a comma, the file may be UTF-8 or windows-1254, and the real
/// header is usually several rows below a block of account details. All three are
/// detected rather than configured, because the user cannot be expected to know.
/// </summary>
public static class StatementReader
{
    /// <summary>Guards against someone feeding a gigabyte of the wrong file.</summary>
    public const long MaxBytes = 12 * 1024 * 1024;

    /// <summary>Header words that mark the real start of the table.</summary>
    private static readonly string[] HeaderHints =
    [
        "TARIH", "DATE", "ACIKLAMA", "DESCRIPTION", "TUTAR", "AMOUNT", "ISLEM",
        "BORC", "ALACAK", "DEBIT", "CREDIT", "BAKIYE", "BALANCE", "REFERANS"
    ];

    static StatementReader() =>
        // windows-1254 is not built into .NET Core; without this the Turkish
        // characters in older bank exports come back as question marks.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static StatementTable Read(byte[] content, string fileName)
    {
        if (content.Length == 0) return StatementTable.Empty;

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" => ReadWorkbook(content),
            _ => ReadDelimited(DecodeText(content))
        };
    }

    /// <summary>UTF-8 when the bytes really are UTF-8, windows-1254 otherwise.</summary>
    public static string DecodeText(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(StripBom(content));
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1254).GetString(content);
        }
    }

    private static byte[] StripBom(byte[] content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF
            ? content[3..]
            : content;

    public static StatementTable ReadDelimited(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        var delimiter = DetectDelimiter(lines);

        var rows = lines
            .Where(l => l.Trim().Length > 0)
            .Select(l => SplitLine(l, delimiter))
            .ToList();

        return BuildTable(rows);
    }

    private static StatementTable ReadWorkbook(byte[] content)
    {
        using var stream = new MemoryStream(content);
        using var workbook = new XLWorkbook(stream);

        var sheet = workbook.Worksheets.FirstOrDefault();
        if (sheet is null) return StatementTable.Empty;

        var used = sheet.RangeUsed();
        if (used is null) return StatementTable.Empty;

        var rows = new List<string[]>();
        foreach (var row in used.RowsUsed())
        {
            var cells = row.Cells(1, used.ColumnCount())
                .Select(CellText)
                .ToArray();

            if (cells.Any(c => c.Length > 0)) rows.Add(cells);
        }

        return BuildTable(rows);
    }

    /// <summary>
    /// Dates are written back in ISO so the date parser sees an unambiguous value;
    /// numbers keep an invariant point that the money parser understands.
    /// </summary>
    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";

        if (cell.DataType == XLDataType.DateTime && cell.Value.IsDateTime)
            return cell.Value.GetDateTime().ToString("yyyy-MM-dd");

        if (cell.DataType == XLDataType.Number && cell.Value.IsNumber)
            return cell.Value.GetNumber().ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

        return cell.GetFormattedString().Trim();
    }

    /// <summary>The separator that yields the most columns consistently across the file.</summary>
    public static char DetectDelimiter(IEnumerable<string> lines)
    {
        var sample = lines.Where(l => l.Trim().Length > 0).Take(30).ToList();
        if (sample.Count == 0) return ';';

        var best = ';';
        var bestScore = -1;

        foreach (var candidate in new[] { ';', ',', '\t', '|' })
        {
            var counts = sample.Select(l => SplitLine(l, candidate).Length).ToList();
            var fields = counts.Max();
            if (fields < 2) continue;

            // Consistency matters more than raw column count: a comma inside an
            // address would otherwise beat the real semicolon separator.
            var consistent = counts.Count(c => c == fields);
            var score = fields * 100 + consistent;

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>RFC 4180 style: quotes protect the delimiter, "" is a literal quote.</summary>
    public static string[] SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return [.. fields];
    }

    /// <summary>
    /// Picks the header row and drops the bank's letterhead above it. Falls back to
    /// the widest early row when nothing recognisable is found, which still gives
    /// the user something to map by hand.
    /// </summary>
    private static StatementTable BuildTable(List<string[]> rows)
    {
        if (rows.Count == 0) return StatementTable.Empty;

        var headerIndex = -1;
        for (var i = 0; i < Math.Min(rows.Count, 25); i++)
        {
            var filled = rows[i].Count(c => c.Length > 0);
            if (filled < 2) continue;

            var hits = rows[i].Count(c => HeaderHints.Any(h => StatementText.Normalize(c).Contains(h)));
            if (hits >= 2)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
            headerIndex = rows.FindIndex(r => r.Count(c => c.Length > 0) >= 2);

        if (headerIndex < 0) return StatementTable.Empty;

        var header = rows[headerIndex];
        var columns = header
            .Select((c, i) => c.Length > 0 ? c : $"Sütun {i + 1}")
            .ToList();

        var body = rows.Skip(headerIndex + 1)
            .Where(r => r.Any(c => c.Length > 0))
            .ToList();

        return new StatementTable(columns, body);
    }
}
