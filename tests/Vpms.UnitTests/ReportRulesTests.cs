using System.Text;
using Vpms.Reporting.Application;
using Vpms.Reporting.Domain;
using Vpms.Reporting.Infrastructure;

namespace Vpms.UnitTests;

public sealed class ReportRulesTests
{
    [Theory]
    [InlineData(-5, "NotDue")]
    [InlineData(0, "NotDue")]
    [InlineData(1, "D1To30")]
    [InlineData(30, "D1To30")]
    [InlineData(31, "D31To60")]
    [InlineData(60, "D31To60")]
    [InlineData(61, "D61To90")]
    [InlineData(90, "D61To90")]
    [InlineData(91, "Over90")]
    public void Aging_buckets_have_exact_edges(int days, string bucket) => Assert.Equal(bucket, AgingBucket.Of(days));

    [Fact]
    public void Weeks_start_on_monday()
    {
        Assert.Equal(new DateOnly(2026, 10, 19), ReportDates.WeekStart(new DateOnly(2026, 10, 19))); // Monday
        Assert.Equal(new DateOnly(2026, 10, 19), ReportDates.WeekStart(new DateOnly(2026, 10, 25))); // Sunday
        Assert.Equal(new DateOnly(2026, 10, 26), ReportDates.WeekStart(new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void Hijri_dates_come_from_the_umm_al_qura_calendar_and_out_of_range_dates_give_nothing()
    {
        Assert.Equal("1447-01-01", ReportDates.Hijri(new DateOnly(2025, 6, 26)));
        Assert.Null(ReportDates.Hijri(new DateOnly(1800, 1, 1)));
    }

    [Fact]
    public void Every_report_and_column_has_wording_in_both_languages()
    {
        foreach (var key in ReportKeys.All)
        {
            Assert.NotEqual(key, ReportLabels.Get("en", key));
            Assert.Matches(@"\p{IsArabic}", ReportLabels.Get("ar", key));
            Assert.Matches(@"\p{IsArabic}", ReportLabels.Get("ar", key + ".desc"));
        }

        foreach (var bucket in AgingBucket.Order) Assert.Matches(@"[\p{IsArabic}]", ReportLabels.Get("ar", "bucket." + bucket));
        foreach (var stage in new[] { "NoRequest", "Created", "InProposal" }) Assert.Matches(@"\p{IsArabic}", ReportLabels.Get("ar", "stage." + stage));
    }

    private static ReportResult Result(params object?[][] rows) => new("r", "R", "en", "gregorian", DateTimeOffset.UtcNow, new Dictionary<string, string>(), false,
        [new ReportTable("detail", "Detail", [new ReportColumn("name", "Name", ColumnType.Text), new ReportColumn("amount", "Amount", ColumnType.Money), new ReportColumn("when", "When", ColumnType.Date), new ReportColumn("flag", "Flag", ColumnType.Flag)],
            rows.Select(r => (IReadOnlyList<object?>)r).ToList())]);

    [Fact]
    public void Csv_keeps_numbers_plain_quotes_text_and_defuses_formulas()
    {
        var bytes = new CsvReportExporter().Export(Result(["=HYPERLINK(\"x\")", -5.5m, new DateOnly(2026, 10, 1), true], ["شركة, المثال", 1234.5m, null, false]), null);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("\"Name\",\"Amount\",\"When\",\"Flag\"", lines[0]);
        Assert.Equal("\"'=HYPERLINK(\"\"x\"\")\",-5.50,2026-10-01,\"Yes\"", lines[1]);
        Assert.Equal("\"شركة, المثال\",1234.50,,\"No\"", lines[2]);
    }

    [Fact]
    public void Hijri_companions_follow_each_date_column_and_keep_the_gregorian_date()
    {
        var table = ReportHijri.WithHijri(Result(["a", 1m, new DateOnly(2025, 6, 26), true]).Tables[0], "(Hijri)");
        Assert.Equal(["name", "amount", "when", "whenHijri", "flag"], table.Columns.Select(c => c.Key).ToArray());
        Assert.Equal("When (Hijri)", table.Columns[3].Label);
        Assert.Equal(new DateOnly(2025, 6, 26), table.Rows[0][2]);
        Assert.Equal("1447-01-01", table.Rows[0][3]);
    }
}
