using System.Globalization;
using System.Reflection;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Vpms.Reporting.Application;

namespace Vpms.Reporting.Infrastructure;

/// <summary>
/// A printable report. The document is laid out right-to-left for Arabic and uses a bundled font that has Arabic letters, so the PDF
/// looks the same wherever it is opened. QuestPDF is used under its community licence; see ADR-0021 for what that means for customers.
/// </summary>
public sealed class PdfReportExporter : IReportExporter
{
    private const string FontFamily = "DejaVu Sans";
    private static readonly object Gate = new();
    private static bool _ready;

    public string Format => "pdf";

    public string ContentType => "application/pdf";

    public string Extension => "pdf";

    public byte[] Export(ReportResult result, string? part)
    {
        Prepare();
        var rtl = result.Locale == "ar";
        var tables = (part is null ? result.Tables : result.Tables.Where(t => t.Name == part)).ToList();

        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontFamily(FontFamily).FontSize(8.5f));
            if (rtl) page.ContentFromRightToLeft(); else page.ContentFromLeftToRight();

            page.Header().Column(col =>
            {
                col.Item().Text(result.Title).FontSize(16).Bold();
                if (result.Filters.Count > 0) col.Item().Text(string.Join("    ", result.Filters.Select(f => $"{f.Key}: {f.Value}"))).FontColor(Colors.Grey.Darken2);
                col.Item().PaddingBottom(6).Text($"{ReportLabels.Get(result.Locale, "generated")}: {result.GeneratedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC").FontColor(Colors.Grey.Darken2);
            });

            page.Content().Column(col =>
            {
                col.Spacing(12);
                if (result.Truncated) col.Item().Text(ReportLabels.Get(result.Locale, "truncated")).Bold().FontColor(Colors.Red.Darken2);
                foreach (var table in tables)
                {
                    col.Item().Text(table.Title).FontSize(11).Bold();
                    col.Item().Element(c => Render(c, table, result.Locale, rtl));
                }
            });

            page.Footer().AlignCenter().Text(text =>
            {
                text.CurrentPageNumber();
                text.Span(" / ");
                text.TotalPages();
            });
        }));

        return document.GeneratePdf();
    }

    private static void Render(IContainer container, ReportTable table, string locale, bool rtl)
    {
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                foreach (var column in table.Columns) c.RelativeColumn(column.Type is ColumnType.Text ? 2 : 1);
            });

            t.Header(h =>
            {
                foreach (var column in table.Columns) h.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text(column.Label).Bold();
            });

            foreach (var row in table.Rows)
            {
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(Isolate(Cell(row[i], table.Columns[i], locale), rtl));
                }
            }
        });
    }

    /// <summary>
    /// In a right-to-left page, text with no Arabic letters (numbers, dates, codes, an English name) is isolated left-to-right so a minus
    /// sign or a mixed identifier is not shuffled to the wrong end.
    /// </summary>
    private static string Isolate(string text, bool rtl) =>
        rtl && text.Length > 0 && !text.Any(ch => ch is >= '\u0600' and <= '\u06FF') ? "\u2066" + text + "\u2069" : text;

    private static string Cell(object? value, ReportColumn column, string locale) => value switch
    {
        null => "",
        string s => s,
        bool b => ReportLabels.Get(locale, b ? "yes" : "no"),
        decimal d => d.ToString(column.Type == ColumnType.Money ? "#,##0.00" : "0.0#", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static void Prepare()
    {
        lock (Gate)
        {
            if (_ready) return;
            QuestPDF.Settings.License = LicenseType.Community;
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                FontManager.RegisterFontFromStream(stream);
            }

            _ready = true;
        }
    }
}
