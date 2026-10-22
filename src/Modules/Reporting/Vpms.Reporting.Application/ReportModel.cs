using Vpms.Reporting.Domain;

namespace Vpms.Reporting.Application;

public static class ColumnType
{
    public const string Text = "text";
    public const string Integer = "int";
    public const string Number = "number";
    public const string Money = "money";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Flag = "flag";
}

public sealed record ReportColumn(string Key, string Label, string Type);

/// <summary>A table of typed cells: string, int, decimal, DateOnly, DateTimeOffset, bool or null.</summary>
public sealed record ReportTable(string Name, string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

public sealed record ReportResult(string Key, string Title, string Locale, string Calendar, DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, string> Filters, bool Truncated, IReadOnlyList<ReportTable> Tables);

public sealed record ReportRequest(string Report, Guid? LegalEntityId, DateOnly? From, DateOnly? To, DateOnly? AsOf, string Locale, string Calendar);

public sealed record ReportInfo(string Key, string Title, string Description, bool NeedsPeriod, bool NeedsTaxPermission);

public sealed record ReportFile(byte[] Content, string FileName, string ContentType, string Sha256, int Rows);

public interface IReportService
{
    IReadOnlyList<ReportInfo> Catalog(string locale);

    /// <param name="entityScope">The legal entities the caller may see; null means every entity of the tenant.</param>
    Task<ReportResult> RunAsync(ReportRequest request, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    /// <summary>Runs the report and renders one table of it in the format. The export is audited (who, what, filters, row count, hash).</summary>
    Task<ReportFile> ExportAsync(ReportRequest request, string format, string? part, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);
}

public interface IReportExporter
{
    string Format { get; }

    string ContentType { get; }

    string Extension { get; }

    /// <param name="part">The table name to render, or null for the exporter's default (CSV: the first table; XLSX/PDF: all).</param>
    byte[] Export(ReportResult result, string? part);
}

public static class ReportLimits
{
    public const int MaxRows = 20_000;
}

public static class ReportHijri
{
    /// <summary>For a Hijri request, puts the Umm al-Qura date in a companion column after each date column (the Gregorian date is always kept).</summary>
    public static ReportTable WithHijri(ReportTable table, string hijriSuffix)
    {
        var columns = new List<ReportColumn>();
        var dateAt = new List<int>();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            columns.Add(table.Columns[i]);
            if (table.Columns[i].Type != ColumnType.Date) continue;
            dateAt.Add(i);
            columns.Add(new ReportColumn(table.Columns[i].Key + "Hijri", table.Columns[i].Label + " " + hijriSuffix, ColumnType.Text));
        }

        if (dateAt.Count == 0) return table;
        var rows = table.Rows.Select(row =>
        {
            var cells = new List<object?>();
            for (var i = 0; i < row.Count; i++)
            {
                cells.Add(row[i]);
                if (dateAt.Contains(i)) cells.Add(row[i] is DateOnly d ? ReportDates.Hijri(d) : null);
            }

            return (IReadOnlyList<object?>)cells;
        }).ToList();
        return table with { Columns = columns, Rows = rows };
    }
}
