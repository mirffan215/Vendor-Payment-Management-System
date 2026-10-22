using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.BuildingBlocks.Errors;
using Vpms.Payments.Infrastructure;
using Vpms.Reporting.Application;
using Vpms.Reporting.Domain;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ReportingTests(SqlServerFixture sql) : PaymentScenarioBase(sql)
{
    private Task<T> Rep<T>(Guid tenant, Guid actor, Func<IReportService, Task<T>> action) => W.AsAsync(tenant, actor, s => action(s.GetRequiredService<IReportService>()));

    private Task<ReportResult> Run(string report, IReadOnlySet<Guid>? scope = null, Action<ReportRequestBuilder>? tweak = null)
    {
        var b = new ReportRequestBuilder(report);
        tweak?.Invoke(b);
        return Rep(W.TenantA, Preparer, s => s.RunAsync(b.Build(), scope));
    }

    private sealed class ReportRequestBuilder(string report)
    {
        public Guid? Entity; public DateOnly? From, To, AsOf; public string Locale = "en", Calendar = "gregorian";

        public ReportRequest Build() => new(report, Entity, From, To, AsOf, Locale, Calendar);
    }

    private static ReportTable Table(ReportResult r, string name) => r.Tables.Single(t => t.Name == name);

    private static object? Cell(ReportTable t, int row, string key) => t.Rows[row][t.Columns.ToList().FindIndex(c => c.Key == key)];

    [Fact]
    public async Task Aging_groups_unpaid_approved_invoices_by_how_late_they_are_per_currency()
    {
        await ApprovedInvoice("INV-1");
        await ApprovedInvoice("INV-2");
        var today = TestDates.Today;

        var now = await Run(ReportKeys.PayablesAging, null, b => b.AsOf = today);
        var summary = Table(now, "summary");
        Assert.Single(summary.Rows);
        Assert.Equal("Not yet due", Cell(summary, 0, "bucket"));
        Assert.Equal(2, Cell(summary, 0, "count"));
        Assert.Equal(230m, Cell(summary, 0, "total"));

        var later = await Run(ReportKeys.PayablesAging, null, b => b.AsOf = today.AddDays(55)); // due about 20 days from today
        Assert.Equal("31-60 days", Cell(Table(later, "summary"), 0, "bucket"));
        var detail = Table(later, "detail");
        Assert.All(detail.Rows, r => Assert.True((int)r[detail.Columns.ToList().FindIndex(c => c.Key == "daysPastDue")]! > 30));
        Assert.Equal("Al Amal Trading", Cell(detail, 0, "vendor"));
    }

    [Fact]
    public async Task A_paid_invoice_drops_out_and_the_payment_stage_shows_in_the_forecast()
    {
        var a = await ApprovedInvoice("INV-1");
        var b = await ApprovedInvoice("INV-2");
        await ApprovedInvoice("INV-3");
        var requested = await RequestFor(a);
        await W.AsAsync(W.TenantA, W.Admin, async s =>
        {
            var db = s.GetRequiredService<PaymentsDbContext>();
            (await db.Requests.SingleAsync(x => x.Id == requested.Id)).Status = "Released";
            return await db.SaveChangesAsync();
        });
        await RequestFor(b);

        var aging = await Run(ReportKeys.PayablesAging);
        Assert.Equal(2, Table(aging, "detail").Rows.Count);

        var forecast = Table(await Run(ReportKeys.PaymentForecast), "summary");
        var stages = forecast.Rows.Select(r => (string)r[forecast.Columns.ToList().FindIndex(c => c.Key == "stage")]!).ToList();
        Assert.Contains("Payment request created", stages);
        Assert.Contains("No payment request yet", stages);
    }

    [Fact]
    public async Task Approval_delays_report_how_long_tasks_waited_and_who_decided()
    {
        await ApprovedInvoice("INV-1");
        var result = await Run(ReportKeys.ApprovalDelays, null, b => { b.From = TestDates.Ago(1); b.To = TestDates.Today; });
        var detail = Table(result, "detail");
        Assert.Single(detail.Rows);
        Assert.Equal("Completed", Cell(detail, 0, "status"));
        Assert.Equal(false, Cell(detail, 0, "escalated"));
        Assert.Contains("INV-1", (string)Cell(detail, 0, "subject")!);
        var summary = Table(result, "summary");
        Assert.Equal(1, Cell(summary, 0, "tasks"));
        Assert.Equal(1, Cell(summary, 0, "done"));
    }

    [Fact]
    public async Task The_vat_register_lists_received_invoices_of_the_period_with_amounts_per_currency()
    {
        await ApprovedInvoice("INV-1", 200m);
        var inPeriod = await Run(ReportKeys.VatRegister, null, b => { b.From = TestDates.Ago(30); b.To = TestDates.Today; });
        var detail = Table(inPeriod, "detail");
        Assert.Single(detail.Rows);
        Assert.Equal(200m, Cell(detail, 0, "net"));
        Assert.Equal(30m, Cell(detail, 0, "vat"));
        Assert.Equal(230m, Cell(detail, 0, "gross"));
        Assert.Equal("S", Cell(detail, 0, "taxCategories"));
        Assert.Equal(230m, Cell(Table(inPeriod, "summary"), 0, "gross"));

        var outside = await Run(ReportKeys.VatRegister, null, b => { b.From = TestDates.Ago(200); b.To = TestDates.Ago(100); });
        Assert.Empty(Table(outside, "detail").Rows);
    }

    [Fact]
    public async Task Exceptions_report_runs_even_when_there_are_none()
    {
        var result = await Run(ReportKeys.Exceptions);
        Assert.Empty(Table(result, "detail").Rows);
        Assert.Equal("Rule", Table(result, "summary").Columns[0].Label);
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_this_and_an_entity_outside_the_scope_is_refused()
    {
        await ApprovedInvoice("INV-1");
        var userB = await W.UserAsync(W.TenantB, "repb@t.com", null, "Report Viewer");
        foreach (var key in ReportKeys.All)
        {
            var other = await Rep(W.TenantB, userB, s => s.RunAsync(new ReportRequest(key, null, TestDates.Ago(30), TestDates.Today, null, "en", "gregorian"), null));
            Assert.All(other.Tables.Where(t => t.Name == "detail"), t => Assert.Empty(t.Rows));
        }

        var onlyOther = new HashSet<Guid> { Guid.NewGuid() };
        var none = await Run(ReportKeys.PayablesAging, onlyOther);
        Assert.Empty(Table(none, "detail").Rows);
        await Assert.ThrowsAsync<NotFoundException>(() => Run(ReportKeys.PayablesAging, onlyOther, b => b.Entity = Entity));
        var allowed = await Run(ReportKeys.PayablesAging, new HashSet<Guid> { Entity });
        Assert.Single(Table(allowed, "detail").Rows);
    }

    [Fact]
    public async Task Hijri_requests_add_a_companion_column_after_each_date_and_arabic_requests_use_arabic_words()
    {
        await ApprovedInvoice("INV-1");
        var hijri = await Run(ReportKeys.PayablesAging, null, b => { b.Calendar = "hijri"; b.Locale = "ar"; });
        Assert.Equal("أعمار الذمم الدائنة", hijri.Title);
        var detail = Table(hijri, "detail");
        var keys = detail.Columns.Select(c => c.Key).ToList();
        Assert.Equal(keys.IndexOf("invoiceDate") + 1, keys.IndexOf("invoiceDateHijri"));
        Assert.Matches(@"^1\d{3}-\d{2}-\d{2}$", (string)Cell(detail, 0, "invoiceDateHijri")!);
        Assert.Equal("المورد", detail.Columns.Single(c => c.Key == "vendor").Label);
    }

    [Fact]
    public async Task Exporting_to_csv_is_audited_with_a_hash_and_carries_the_byte_order_mark()
    {
        await ApprovedInvoice("INV-1");
        var file = await Rep(W.TenantA, Preparer, s => s.ExportAsync(new ReportRequest(ReportKeys.PayablesAging, null, null, null, null, "en", "gregorian"), "csv", "detail", null));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.Content.Take(3).ToArray());
        Assert.EndsWith(".csv", file.FileName);
        Assert.Equal(1, file.Rows);

        var audit = await W.AsAsync(W.TenantA, W.Admin, s => s.GetRequiredService<Vpms.Audit.Infrastructure.AuditDbContext>().Events.AsNoTracking()
            .Where(e => e.Action == "report.exported").Select(e => e.ChangeSummaryJson).ToListAsync());
        Assert.Contains(audit, a => a!.Contains(file.Sha256));

        await Assert.ThrowsAsync<ValidationFailedException>(() => Rep(W.TenantA, Preparer, s => s.ExportAsync(new ReportRequest(ReportKeys.PayablesAging, null, null, null, null, "en", "gregorian"), "docx", null, null)));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Rep(W.TenantA, Preparer, s => s.ExportAsync(new ReportRequest(ReportKeys.PayablesAging, null, null, null, null, "en", "gregorian"), "csv", "nope", null)));
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_or_is_far_too_long_is_refused()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => Run(ReportKeys.Exceptions, null, b => { b.From = TestDates.Today; b.To = TestDates.Ago(5); }));
        await Assert.ThrowsAsync<ValidationFailedException>(() => Run(ReportKeys.Exceptions, null, b => { b.From = TestDates.Ago(1500); b.To = TestDates.Today; }));
    }
}
