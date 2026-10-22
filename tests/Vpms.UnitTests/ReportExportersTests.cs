using ClosedXML.Excel;
using Vpms.Reporting.Application;
using Vpms.Reporting.Infrastructure;

namespace Vpms.UnitTests;

public sealed class ReportExportersTests
{
    private static ReportResult Sample(string locale = "en", bool truncated = false)
    {
        var ar = locale == "ar";
        ReportColumn C(string key, string en, string arabic, string type) => new(key, ar ? arabic : en, type);
        var detail = new ReportTable("detail", ar ? "التفاصيل" : "Detail",
            [C("vendor", "Vendor", "المورد", ColumnType.Text), C("invoice", "Invoice number", "رقم الفاتورة", ColumnType.Text), C("due", "Due date", "تاريخ الاستحقاق", ColumnType.Date),
             C("currency", "Currency", "العملة", ColumnType.Text), C("amount", "Amount", "المبلغ", ColumnType.Money), C("late", "Overdue", "متأخر", ColumnType.Flag)],
            [["شركة الأمل للتجارة", "INV-100", new DateOnly(2026, 10, 1), "SAR", 1150.5m, true], ["=HYPERLINK(\"http://evil\")", "+INV", new DateOnly(2026, 11, 3), "USD", -20m, false]]);
        var summary = new ReportTable("summary", ar ? "الملخص" : "Summary", [C("currency", "Currency", "العملة", ColumnType.Text), C("total", "Total", "الإجمالي", ColumnType.Money)], [["SAR", 1150.5m], ["USD", -20m]]);
        return new ReportResult("payables-aging", ar ? "أعمار الذمم الدائنة" : "Payables aging", locale, "gregorian", new DateTimeOffset(2026, 10, 22, 8, 30, 0, TimeSpan.Zero),
            new Dictionary<string, string> { ["entity"] = ar ? "المملكة" : "KSA" }, truncated, [summary, detail]);
    }

    [Fact]
    public void Excel_keeps_types_and_never_turns_text_into_a_formula()
    {
        var bytes = new XlsxReportExporter().Export(Sample(), null);
        using var book = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(2, book.Worksheets.Count);
        var sheet = book.Worksheet("Detail");
        Assert.False(sheet.RightToLeft);
        Assert.Equal("Vendor", sheet.Cell(5, 1).GetString());
        Assert.Equal("شركة الأمل للتجارة", sheet.Cell(6, 1).GetString());
        Assert.Equal(1150.5, sheet.Cell(6, 5).GetDouble());
        Assert.Equal(XLDataType.Number, sheet.Cell(6, 5).DataType);
        Assert.Equal(XLDataType.DateTime, sheet.Cell(6, 3).DataType);
        Assert.Equal("Yes", sheet.Cell(6, 6).GetString());

        var evil = sheet.Cell(7, 1);
        Assert.Equal("=HYPERLINK(\"http://evil\")", evil.GetString());
        Assert.Equal(XLDataType.Text, evil.DataType);
        Assert.False(evil.HasFormula);
        Assert.False(sheet.Cell(7, 2).HasFormula);
    }

    [Fact]
    public void Arabic_workbooks_open_right_to_left_and_one_part_can_be_exported_alone()
    {
        using var all = new XLWorkbook(new MemoryStream(new XlsxReportExporter().Export(Sample("ar"), null)));
        Assert.All(all.Worksheets, s => Assert.True(s.RightToLeft));
        using var one = new XLWorkbook(new MemoryStream(new XlsxReportExporter().Export(Sample("ar"), "summary")));
        Assert.Single(one.Worksheets);
        Assert.Equal("العملة", one.Worksheets.First().Cell(5, 1).GetString());
    }

    [Fact]
    public void A_truncated_report_says_so_in_the_workbook()
    {
        using var book = new XLWorkbook(new MemoryStream(new XlsxReportExporter().Export(Sample(truncated: true), "detail")));
        Assert.Contains(book.Worksheets.First().CellsUsed(), c => c.GetString().StartsWith("Only the first rows"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Pdf_is_a_real_document_with_pages_in_either_language(string locale)
    {
        var bytes = new PdfReportExporter().Export(Sample(locale), null);
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.True(bytes.Length > 5_000);

        // Writes the documents out for a human to look at when asked to (VPMS_SAMPLE_DIR=/some/dir dotnet test ...).
        if (Environment.GetEnvironmentVariable("VPMS_SAMPLE_DIR") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, $"sample-{locale}.pdf"), bytes);
    }

    [Fact]
    public void Exporters_announce_their_format_and_extension()
    {
        Assert.Equal(["csv", "xlsx", "pdf"], new IReportExporter[] { new CsvReportExporter(), new XlsxReportExporter(), new PdfReportExporter() }.Select(e => e.Format).ToArray());
        Assert.Equal("application/pdf", new PdfReportExporter().ContentType);
        Assert.Contains("spreadsheetml", new XlsxReportExporter().ContentType);
    }
}
