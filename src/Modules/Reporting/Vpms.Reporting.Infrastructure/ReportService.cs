using System.Security.Cryptography;
using System.Text.Json;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Invoices.Contracts;
using Vpms.Payments.Contracts;
using Vpms.Reporting.Application;
using Vpms.Reporting.Domain;
using Vpms.Vendors.Contracts;
using Vpms.Workflow.Contracts;

namespace Vpms.Reporting.Infrastructure;

/// <summary>
/// Builds the reports from the read contracts of the modules that own the data. Rows are limited to the caller's legal entities, and
/// money is never added across currencies: every total is per currency.
/// </summary>
public sealed class ReportService(
    IInvoiceReportSource invoices, IApprovalReportSource approvals, IPaymentReportSource payments, IVendorLookup vendors, ILegalEntityLookup entities, IUserDirectory users,
    IEnumerable<IReportExporter> exporters, IAuditPort audit, TimeProvider clock) : IReportService
{
    public IReadOnlyList<ReportInfo> Catalog(string locale) => ReportKeys.All.Select(k => new ReportInfo(
        k, ReportLabels.Get(locale, k), ReportLabels.Get(locale, k + ".desc"), k is ReportKeys.Exceptions or ReportKeys.ApprovalDelays or ReportKeys.VatRegister, k == ReportKeys.VatRegister)).ToList();

    public async Task<ReportResult> RunAsync(ReportRequest request, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var locale = request.Locale == "ar" ? "ar" : "en";
        var calendar = request.Calendar == "hijri" ? "hijri" : "gregorian";
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (request.LegalEntityId is { } e && entityScope is not null && !entityScope.Contains(e)) throw new NotFoundException("Legal entity");

        var asOf = request.AsOf ?? today;
        var to = request.To ?? today;
        var from = request.From ?? to.AddDays(request.Report == ReportKeys.ApprovalDelays ? -30 : -90);
        if (from > to) throw new ValidationFailedException("invalid_period", "The start of the period is after its end.");
        if (to.DayNumber - from.DayNumber > 800) throw new ValidationFailedException("period_too_long", "Choose a period of at most about two years.");

        var (tables, truncated) = request.Report switch
        {
            ReportKeys.PayablesAging => await AgingAsync(request, entityScope, asOf, locale, ct),
            ReportKeys.PaymentForecast => await ForecastAsync(request, entityScope, asOf, to, locale, ct),
            ReportKeys.Exceptions => await ExceptionsAsync(request, entityScope, from, to, today, locale, ct),
            ReportKeys.ApprovalDelays => await DelaysAsync(request, entityScope, from, to, now, locale, ct),
            ReportKeys.VatRegister => await VatAsync(request, entityScope, from, to, locale, ct),
            _ => throw new NotFoundException("Report"),
        };

        if (calendar == "hijri") tables = tables.Select(t => ReportHijri.WithHijri(t, ReportLabels.Get(locale, "hijri"))).ToList();
        var filters = new Dictionary<string, string>();
        if (request.LegalEntityId is { } id) filters["entity"] = (await entities.GetNamesAsync([id], ct)).GetValueOrDefault(id, id.ToString());
        if (request.Report is ReportKeys.PayablesAging or ReportKeys.PaymentForecast) filters["asOf"] = asOf.ToString("yyyy-MM-dd");
        if (request.Report is not ReportKeys.PayablesAging) { filters["from"] = from.ToString("yyyy-MM-dd"); filters["to"] = to.ToString("yyyy-MM-dd"); }
        return new ReportResult(request.Report, ReportLabels.Get(locale, request.Report), locale, calendar, now, filters, truncated, tables);
    }

    public async Task<ReportFile> ExportAsync(ReportRequest request, string format, string? part, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var exporter = exporters.FirstOrDefault(x => string.Equals(x.Format, format, StringComparison.OrdinalIgnoreCase)) ?? throw new ValidationFailedException("format_invalid", "That export format is not available.");
        var result = await RunAsync(request, entityScope, ct);
        if (part is not null && result.Tables.All(t => t.Name != part)) throw new ValidationFailedException("part_invalid", "That part of the report does not exist.");
        var bytes = exporter.Export(result, part);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var rows = (part is null ? result.Tables : result.Tables.Where(t => t.Name == part)).Sum(t => t.Rows.Count);
        await audit.WriteNowAsync(new AuditEntry("report.exported", "Report", request.Report, request.LegalEntityId, JsonSerializer.Serialize(new
        {
            report = request.Report, format = exporter.Format, part, filters = result.Filters, rows, sha256 = hash, result.Truncated,
        })), ct);
        return new ReportFile(bytes, $"{request.Report}-{clock.GetUtcNow():yyyyMMdd-HHmm}.{exporter.Extension}", exporter.ContentType, hash, rows);
    }

    // --- reports ---------------------------------------------------------------------------------------------

    private static ReportColumn C(string locale, string key, string type) => new(key, ReportLabels.Get(locale, "col." + key), type);

    private async Task<(IReadOnlyList<ReportTable>, bool)> AgingAsync(ReportRequest r, IReadOnlySet<Guid>? scope, DateOnly asOf, string locale, CancellationToken ct)
    {
        var (open, truncated) = await OpenUnpaidAsync(r, scope, ct);
        var names = await NamesAsync(open.Select(o => o.Row.LegalEntityId), open.Select(o => o.Row.VendorId), ct);
        var detail = open.Select(o =>
        {
            var due = o.Row.DueDate ?? o.Row.InvoiceDate;
            var days = asOf.DayNumber - due.DayNumber;
            return new { o.Row, Days = days, Bucket = AgingBucket.Of(days) };
        }).OrderByDescending(x => x.Days).ThenBy(x => x.Row.InvoiceNumber, StringComparer.Ordinal).ToList();

        var detailTable = new ReportTable("detail", ReportLabels.Get(locale, "table.detail"),
            [C(locale, "entity", ColumnType.Text), C(locale, "vendor", ColumnType.Text), C(locale, "invoiceNumber", ColumnType.Text), C(locale, "invoiceDate", ColumnType.Date), C(locale, "dueDate", ColumnType.Date),
             C(locale, "noDueDate", ColumnType.Flag), C(locale, "daysPastDue", ColumnType.Integer), C(locale, "bucket", ColumnType.Text), C(locale, "currency", ColumnType.Text), C(locale, "amount", ColumnType.Money)],
            detail.Select(x => (IReadOnlyList<object?>)[names.Entity(x.Row.LegalEntityId), names.Vendor(x.Row.VendorId), x.Row.InvoiceNumber, x.Row.InvoiceDate, x.Row.DueDate, x.Row.DueDate is null,
                Math.Max(0, x.Days), ReportLabels.Get(locale, "bucket." + x.Bucket), x.Row.CurrencyCode, x.Row.Total]).ToList());

        var summary = detail.GroupBy(x => (x.Row.CurrencyCode, x.Bucket)).OrderBy(g => g.Key.CurrencyCode, StringComparer.Ordinal).ThenBy(g => AgingBucket.Order.ToList().IndexOf(g.Key.Bucket))
            .Select(g => (IReadOnlyList<object?>)[g.Key.CurrencyCode, ReportLabels.Get(locale, "bucket." + g.Key.Bucket), g.Count(), g.Sum(x => x.Row.Total)]).ToList();
        var summaryTable = new ReportTable("summary", ReportLabels.Get(locale, "table.summary"),
            [C(locale, "currency", ColumnType.Text), C(locale, "bucket", ColumnType.Text), C(locale, "count", ColumnType.Integer), C(locale, "total", ColumnType.Money)], summary);
        return ([summaryTable, detailTable], truncated);
    }

    private async Task<(IReadOnlyList<ReportTable>, bool)> ForecastAsync(ReportRequest r, IReadOnlySet<Guid>? scope, DateOnly asOf, DateOnly horizon, string locale, CancellationToken ct)
    {
        var (open, truncated) = await OpenUnpaidAsync(r, scope, ct);
        var names = await NamesAsync(open.Select(o => o.Row.LegalEntityId), open.Select(o => o.Row.VendorId), ct);
        var items = open.Select(o => new { o.Row, o.Stage, Due = o.Row.DueDate ?? o.Row.InvoiceDate })
            .Where(x => r.To is null || x.Due <= horizon).OrderBy(x => x.Due).ThenBy(x => x.Row.InvoiceNumber, StringComparer.Ordinal).ToList();

        var detail = new ReportTable("detail", ReportLabels.Get(locale, "table.detail"),
            [C(locale, "entity", ColumnType.Text), C(locale, "vendor", ColumnType.Text), C(locale, "invoiceNumber", ColumnType.Text), C(locale, "dueDate", ColumnType.Date), C(locale, "noDueDate", ColumnType.Flag),
             C(locale, "currency", ColumnType.Text), C(locale, "amount", ColumnType.Money), C(locale, "stage", ColumnType.Text)],
            items.Select(x => (IReadOnlyList<object?>)[names.Entity(x.Row.LegalEntityId), names.Vendor(x.Row.VendorId), x.Row.InvoiceNumber, x.Due, x.Row.DueDate is null, x.Row.CurrencyCode, x.Row.Total,
                ReportLabels.Get(locale, "stage." + x.Stage)]).ToList());

        var thisWeek = ReportDates.WeekStart(asOf);
        var summary = items.GroupBy(x => (x.Row.CurrencyCode, Week: ReportDates.WeekStart(x.Due) < thisWeek ? thisWeek : ReportDates.WeekStart(x.Due), x.Stage))
            .OrderBy(g => g.Key.CurrencyCode, StringComparer.Ordinal).ThenBy(g => g.Key.Week).ThenBy(g => g.Key.Stage, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<object?>)[g.Key.CurrencyCode, g.Key.Week, ReportLabels.Get(locale, "stage." + g.Key.Stage), g.Count(), g.Sum(x => x.Row.Total), g.Any(x => x.Due < thisWeek)]).ToList();
        var summaryTable = new ReportTable("summary", ReportLabels.Get(locale, "table.summary"),
            [C(locale, "currency", ColumnType.Text), C(locale, "week", ColumnType.Date), C(locale, "stage", ColumnType.Text), C(locale, "count", ColumnType.Integer), C(locale, "total", ColumnType.Money),
             C(locale, "includesOverdue", ColumnType.Flag)], summary);
        return ([summaryTable, detail], truncated);
    }

    private async Task<(IReadOnlyList<ReportTable>, bool)> ExceptionsAsync(ReportRequest r, IReadOnlySet<Guid>? scope, DateOnly from, DateOnly to, DateOnly today, string locale, CancellationToken ct)
    {
        var rows = await invoices.ExceptionsAsync(r.LegalEntityId, scope, from, to, ReportLimits.MaxRows + 1, ct);
        var truncated = rows.Count > ReportLimits.MaxRows;
        rows = rows.Take(ReportLimits.MaxRows).ToList();
        var names = await NamesAsync(rows.Select(x => x.LegalEntityId), [], ct);
        var people = await users.GetAsync(rows.Where(x => x.AssignedTo.HasValue).Select(x => x.AssignedTo!.Value).Distinct(), ct);

        var detail = new ReportTable("detail", ReportLabels.Get(locale, "table.detail"),
            [C(locale, "entity", ColumnType.Text), C(locale, "invoiceNumber", ColumnType.Text), C(locale, "rule", ColumnType.Text), C(locale, "severity", ColumnType.Text), C(locale, "status", ColumnType.Text),
             C(locale, "createdAt", ColumnType.Date), C(locale, "resolvedAt", ColumnType.Date), C(locale, "ageDays", ColumnType.Integer), C(locale, "resolution", ColumnType.Text), C(locale, "assignedTo", ColumnType.Text)],
            rows.Select(x =>
            {
                var created = DateOnly.FromDateTime(x.CreatedAt.UtcDateTime);
                var end = x.ResolvedAt is { } rv ? DateOnly.FromDateTime(rv.UtcDateTime) : today;
                return (IReadOnlyList<object?>)[names.Entity(x.LegalEntityId), x.InvoiceNumber, x.RuleCode, ReportLabels.GetOrSelf(locale, "severity." + x.Severity, x.Severity),
                    ReportLabels.GetOrSelf(locale, "status." + x.Status, x.Status), created, x.ResolvedAt is { } r2 ? DateOnly.FromDateTime(r2.UtcDateTime) : null, Math.Max(0, end.DayNumber - created.DayNumber),
                    x.Resolution, x.AssignedTo is { } a && people.TryGetValue(a, out var u) ? u.DisplayName : null];
            }).ToList());

        var summary = rows.GroupBy(x => (x.RuleCode, x.Severity)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.RuleCode, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<object?>)[g.Key.RuleCode, ReportLabels.GetOrSelf(locale, "severity." + g.Key.Severity, g.Key.Severity), g.Count(x => x.Status == "Open"), g.Count(x => x.Status != "Open")]).ToList();
        var summaryTable = new ReportTable("summary", ReportLabels.Get(locale, "table.summary"),
            [C(locale, "rule", ColumnType.Text), C(locale, "severity", ColumnType.Text), C(locale, "open", ColumnType.Integer), C(locale, "closed", ColumnType.Integer)], summary);
        return ([summaryTable, detail], truncated);
    }

    private async Task<(IReadOnlyList<ReportTable>, bool)> DelaysAsync(ReportRequest r, IReadOnlySet<Guid>? scope, DateOnly from, DateOnly to, DateTimeOffset now, string locale, CancellationToken ct)
    {
        var rows = await approvals.TasksAsync(r.LegalEntityId, scope, from, to, ReportLimits.MaxRows + 1, ct);
        var truncated = rows.Count > ReportLimits.MaxRows;
        rows = rows.Take(ReportLimits.MaxRows).ToList();
        var names = await NamesAsync(rows.Select(x => x.LegalEntityId), [], ct);
        var people = await users.GetAsync(rows.SelectMany(x => x.Assignees).Concat(rows.Where(x => x.DecidedBy.HasValue).Select(x => x.DecidedBy!.Value)).Distinct(), ct);
        string Person(Guid id) => people.TryGetValue(id, out var u) ? u.DisplayName : id.ToString();

        var items = rows.Select(x =>
        {
            var end = x.CompletedAt ?? now;
            var hours = Math.Round((decimal)(end - x.CreatedAt).TotalHours, 1);
            var overdue = x.DueAt is { } d && end > d;
            return new { Row = x, Hours = hours, Overdue = overdue };
        }).ToList();

        var detail = new ReportTable("detail", ReportLabels.Get(locale, "table.detail"),
            [C(locale, "entity", ColumnType.Text), C(locale, "subject", ColumnType.Text), C(locale, "step", ColumnType.Text), C(locale, "assignees", ColumnType.Text), C(locale, "created", ColumnType.DateTime),
             C(locale, "due", ColumnType.DateTime), C(locale, "completed", ColumnType.DateTime), C(locale, "waitHours", ColumnType.Number), C(locale, "overdue", ColumnType.Flag),
             C(locale, "escalated", ColumnType.Flag), C(locale, "decidedBy", ColumnType.Text), C(locale, "status", ColumnType.Text)],
            items.Select(x => (IReadOnlyList<object?>)[names.Entity(x.Row.LegalEntityId), x.Row.SubjectLabel, x.Row.StepId, string.Join(", ", x.Row.Assignees.Select(Person)), x.Row.CreatedAt, x.Row.DueAt, x.Row.CompletedAt,
                x.Hours, x.Overdue, x.Row.EscalatedAt.HasValue, x.Row.DecidedBy is { } by ? Person(by) : null, ReportLabels.GetOrSelf(locale, "status." + x.Row.TaskStatus, x.Row.TaskStatus)]).ToList());

        var summary = items.GroupBy(x => (x.Row.LegalEntityId, x.Row.StepId)).OrderBy(g => names.Entity(g.Key.LegalEntityId), StringComparer.Ordinal).ThenBy(g => g.Key.StepId, StringComparer.Ordinal)
            .Select(g =>
            {
                var done = g.Where(x => x.Row.CompletedAt.HasValue).ToList();
                return (IReadOnlyList<object?>)[names.Entity(g.Key.LegalEntityId), g.Key.StepId, g.Count(), done.Count, done.Count == 0 ? 0m : Math.Round(done.Average(x => x.Hours), 1),
                    g.Max(x => x.Hours), g.Count(x => x.Overdue), g.Count(x => x.Row.EscalatedAt.HasValue)];
            }).ToList();
        var summaryTable = new ReportTable("summary", ReportLabels.Get(locale, "table.summary"),
            [C(locale, "entity", ColumnType.Text), C(locale, "step", ColumnType.Text), C(locale, "tasks", ColumnType.Integer), C(locale, "done", ColumnType.Integer), C(locale, "avgHours", ColumnType.Number),
             C(locale, "maxHours", ColumnType.Number), C(locale, "overdueCount", ColumnType.Integer), C(locale, "escalatedCount", ColumnType.Integer)], summary);
        return ([summaryTable, detail], truncated);
    }

    private async Task<(IReadOnlyList<ReportTable>, bool)> VatAsync(ReportRequest r, IReadOnlySet<Guid>? scope, DateOnly from, DateOnly to, string locale, CancellationToken ct)
    {
        var rows = await invoices.VatRegisterAsync(r.LegalEntityId, scope, from, to, ReportLimits.MaxRows + 1, ct);
        var truncated = rows.Count > ReportLimits.MaxRows;
        rows = rows.Take(ReportLimits.MaxRows).ToList();
        var names = await NamesAsync(rows.Select(x => x.LegalEntityId), rows.Select(x => x.VendorId), ct);

        var detail = new ReportTable("detail", ReportLabels.Get(locale, "table.detail"),
            [C(locale, "entity", ColumnType.Text), C(locale, "vendor", ColumnType.Text), C(locale, "supplierTaxNumber", ColumnType.Text), C(locale, "invoiceNumber", ColumnType.Text), C(locale, "invoiceDate", ColumnType.Date),
             C(locale, "einvoiceRef", ColumnType.Text), C(locale, "currency", ColumnType.Text), C(locale, "net", ColumnType.Money), C(locale, "vat", ColumnType.Money), C(locale, "gross", ColumnType.Money),
             C(locale, "taxCategories", ColumnType.Text), C(locale, "status", ColumnType.Text), C(locale, "waived", ColumnType.Flag)],
            rows.Select(x => (IReadOnlyList<object?>)[names.Entity(x.LegalEntityId), names.Vendor(x.VendorId), x.SupplierTaxNumber, x.InvoiceNumber, x.InvoiceDate, x.SupplierTaxInvoiceRef, x.CurrencyCode,
                x.Subtotal, x.TaxTotal, x.Total, x.TaxCategories, ReportLabels.GetOrSelf(locale, "status." + x.Status, x.Status), x.HasWaivedException]).ToList());

        var summary = rows.GroupBy(x => x.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<object?>)[g.Key, g.Count(), g.Sum(x => x.Subtotal), g.Sum(x => x.TaxTotal), g.Sum(x => x.Total)]).ToList();
        var summaryTable = new ReportTable("summary", ReportLabels.Get(locale, "table.summary"),
            [C(locale, "currency", ColumnType.Text), C(locale, "count", ColumnType.Integer), C(locale, "net", ColumnType.Money), C(locale, "vat", ColumnType.Money), C(locale, "gross", ColumnType.Money)], summary);
        return ([summaryTable, detail], truncated);
    }

    // --- shared ----------------------------------------------------------------------------------------------

    private sealed record Open(OpenPayableRow Row, string Stage);

    /// <summary>Approved invoices whose payment has not been released, each with how far payment preparation has got.</summary>
    private async Task<(List<Open>, bool)> OpenUnpaidAsync(ReportRequest r, IReadOnlySet<Guid>? scope, CancellationToken ct)
    {
        var rows = await invoices.OpenPayablesAsync(r.LegalEntityId, scope, ReportLimits.MaxRows + 1, ct);
        var truncated = rows.Count > ReportLimits.MaxRows;
        rows = rows.Take(ReportLimits.MaxRows).ToList();
        var status = await payments.RequestStatusByInvoiceAsync(rows.Select(x => x.InvoiceId).ToList(), ct);
        var open = rows.Where(x => status.GetValueOrDefault(x.InvoiceId) != "Released").Select(x => new Open(x, PaymentStage.Of(status.GetValueOrDefault(x.InvoiceId)))).ToList();
        return (open, truncated);
    }

    private sealed record Names(IReadOnlyDictionary<Guid, string> Entities, IReadOnlyDictionary<Guid, string> Vendors)
    {
        public string Entity(Guid id) => Entities.GetValueOrDefault(id, id.ToString());

        public string Vendor(Guid id) => Vendors.GetValueOrDefault(id, id.ToString());
    }

    private async Task<Names> NamesAsync(IEnumerable<Guid> entityIds, IEnumerable<Guid> vendorIds, CancellationToken ct)
    {
        var entityNames = await entities.GetNamesAsync(entityIds.Distinct().ToList(), ct);
        var vendorNames = new Dictionary<Guid, string>();
        foreach (var id in vendorIds.Distinct())
        {
            if (await vendors.FindAsync(id, ct) is { } v) vendorNames[id] = v.LegalName;
        }

        return new Names(entityNames, vendorNames);
    }
}
