using ClosedXML.Excel;
using Vpms.Reporting.Application;

namespace Vpms.Reporting.Infrastructure;

/// <summary>
/// One worksheet per table. Text is always stored as text (never as a formula), numbers and dates keep their types so the sheet can be
/// summed and sorted, and Arabic reports open right-to-left.
/// </summary>
public sealed class XlsxReportExporter : IReportExporter
{
    public string Format => "xlsx";

    public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public string Extension => "xlsx";

    public byte[] Export(ReportResult result, string? part)
    {
        using var book = new XLWorkbook();
        var rtl = result.Locale == "ar";
        foreach (var table in part is null ? result.Tables : result.Tables.Where(t => t.Name == part))
        {
            var sheet = book.Worksheets.Add(SheetName(table.Title));
            sheet.RightToLeft = rtl;
            sheet.Cell(1, 1).SetValue(result.Title);
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).SetValue(string.Join("   ", result.Filters.Select(f => $"{f.Key}: {f.Value}")));
            sheet.Cell(3, 1).SetValue($"{ReportLabels.Get(result.Locale, "generated")}: {result.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            const int headerRow = 5;
            for (var c = 0; c < table.Columns.Count; c++)
            {
                var cell = sheet.Cell(headerRow, c + 1);
                cell.SetValue(table.Columns[c].Label);
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            for (var r = 0; r < table.Rows.Count; r++)
            {
                for (var c = 0; c < table.Columns.Count; c++) Write(sheet.Cell(headerRow + 1 + r, c + 1), table.Rows[r][c], table.Columns[c], result.Locale);
            }

            if (result.Truncated) sheet.Cell(headerRow + table.Rows.Count + 2, 1).SetValue(ReportLabels.Get(result.Locale, "truncated"));
            sheet.SheetView.FreezeRows(headerRow);
            sheet.Columns(1, table.Columns.Count).AdjustToContents(headerRow, headerRow + Math.Min(table.Rows.Count, 200) + 1, 8, 60);
        }

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void Write(IXLCell cell, object? value, ReportColumn column, string locale)
    {
        switch (value)
        {
            case null: break;
            case string s: cell.SetValue(s); break; // a text cell, even when it starts with "=": never evaluated
            case bool b: cell.SetValue(ReportLabels.Get(locale, b ? "yes" : "no")); break;
            case decimal d: cell.SetValue(d); cell.Style.NumberFormat.Format = column.Type == ColumnType.Money ? "#,##0.00" : "#,##0.0#"; break;
            case int i: cell.SetValue(i); break;
            case long l: cell.SetValue(l); break;
            case DateOnly d: cell.SetValue(d.ToDateTime(TimeOnly.MinValue)); cell.Style.NumberFormat.Format = "yyyy-mm-dd"; break;
            case DateTimeOffset t: cell.SetValue(t.UtcDateTime); cell.Style.NumberFormat.Format = "yyyy-mm-dd hh:mm"; break;
            default: cell.SetValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""); break;
        }
    }

    /// <summary>Excel limits sheet names to 31 characters and forbids a few characters.</summary>
    private static string SheetName(string title)
    {
        var clean = new string(title.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        return clean.Length == 0 ? "Sheet" : clean[..Math.Min(clean.Length, 31)];
    }
}
