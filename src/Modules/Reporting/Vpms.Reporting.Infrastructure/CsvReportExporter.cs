using System.Globalization;
using System.Text;
using Vpms.Reporting.Application;

namespace Vpms.Reporting.Infrastructure;

/// <summary>
/// UTF-8 with a byte-order mark (so Excel reads Arabic correctly), CRLF line ends, every text cell quoted, and text that a spreadsheet
/// could read as a formula prefixed with an apostrophe. Numbers and dates are written plainly and never prefixed.
/// </summary>
public sealed class CsvReportExporter : IReportExporter
{
    public string Format => "csv";

    public string ContentType => "text/csv; charset=utf-8";

    public string Extension => "csv";

    public byte[] Export(ReportResult result, string? part)
    {
        var table = part is null ? result.Tables[0] : result.Tables.First(t => t.Name == part);
        var sb = new StringBuilder();
        sb.Append(string.Join(',', table.Columns.Select(c => Cell(c.Label)))).Append("\r\n");
        foreach (var row in table.Rows)
        {
            sb.Append(string.Join(',', row.Select((v, i) => Value(v, table.Columns[i], result.Locale)))).Append("\r\n");
        }

        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static string Value(object? value, ReportColumn column, string locale) => value switch
    {
        null => "",
        string s => Cell(s),
        bool b => Cell(ReportLabels.Get(locale, b ? "yes" : "no")),
        decimal d => d.ToString(column.Type == ColumnType.Money ? "0.00" : "0.0#", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC",
        _ => Cell(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>Quotes a text cell and defuses a leading character that makes a spreadsheet treat it as a formula.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
