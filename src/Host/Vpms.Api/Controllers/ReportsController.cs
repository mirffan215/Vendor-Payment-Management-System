using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Vpms.Api.Auth;
using Vpms.Identity.Contracts;
using Vpms.Reporting.Application;
using Vpms.Reporting.Domain;

namespace Vpms.Api.Controllers;

/// <summary>Reports over the data of the caller's legal entities, with CSV, Excel and PDF export. Every export is audited.</summary>
[ApiController, ApiVersion("1.0"), Route("api/v{version:apiVersion}/reports")]
public sealed class ReportsController(IReportService reports, IPermissionEvaluator permissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Catalog([FromQuery] string locale = "en", CancellationToken ct = default)
    {
        var (allowed, _) = await this.ScopeForAsync(permissions, Permissions.ReportView, ct);
        if (!allowed) return Forbid();
        var hasTax = (await this.ScopeForAsync(permissions, Permissions.ReportTax, ct)).Allowed;
        return Ok(reports.Catalog(locale).Where(r => hasTax || !r.NeedsTaxPermission));
    }

    [HttpGet("{report}")]
    public async Task<IActionResult> Run(string report, [FromQuery] Guid? legalEntityId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] DateOnly? asOf,
        [FromQuery] string locale = "en", [FromQuery] string calendar = "gregorian", CancellationToken ct = default)
    {
        var scope = await ScopeAsync(report, Permissions.ReportView, ct);
        return scope.Allowed ? Ok(await reports.RunAsync(new ReportRequest(report, legalEntityId, from, to, asOf, locale, calendar), scope.Scope, ct)) : Forbid();
    }

    [HttpGet("{report}/export")]
    public async Task<IActionResult> Export(string report, [FromQuery] string format = "csv", [FromQuery] string? part = null, [FromQuery] Guid? legalEntityId = null, [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null, [FromQuery] DateOnly? asOf = null, [FromQuery] string locale = "en", [FromQuery] string calendar = "gregorian", CancellationToken ct = default)
    {
        var scope = await ScopeAsync(report, Permissions.ReportExport, ct);
        if (!scope.Allowed) return Forbid();
        var file = await reports.ExportAsync(new ReportRequest(report, legalEntityId, from, to, asOf, locale, calendar), format, part, scope.Scope, ct);
        Response.Headers["X-Content-SHA256"] = file.Sha256;
        return File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>The entities the caller may see for the permission; the VAT register also needs the tax permission, over the entities both allow.</summary>
    private async Task<(bool Allowed, IReadOnlySet<Guid>? Scope)> ScopeAsync(string report, string permission, CancellationToken ct)
    {
        if (!ReportKeys.All.Contains(report)) return (false, null);
        var main = await this.ScopeForAsync(permissions, permission, ct);
        if (!main.Allowed || report != ReportKeys.VatRegister) return main;
        var tax = await this.ScopeForAsync(permissions, Permissions.ReportTax, ct);
        if (!tax.Allowed) return (false, null);
        if (main.Scope is null) return tax;
        if (tax.Scope is null) return main;
        var both = main.Scope.Intersect(tax.Scope).ToHashSet();
        return (both.Count > 0, both);
    }

}
