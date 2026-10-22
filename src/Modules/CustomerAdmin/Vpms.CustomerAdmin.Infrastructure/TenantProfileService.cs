using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Application;
using Vpms.Documents.Contracts;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class TenantProfileService(CustomerAdminDbContext db, ITenantContext tenant, IAuditPort audit, IAttachmentService attachments)
    : ITenantProfileService
{
    private static readonly string[] Locales = ["en", "ar"];
    private static readonly string[] Calendars = ["gregorian", "hijri"];

    public async Task<TenantProfile> GetAsync(CancellationToken ct = default) => Map(await Load(ct));

    public async Task<TenantProfile> UpdateAsync(UpdateTenantProfile update, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.Name) || update.Name.Length > 200)
        {
            throw new ValidationFailedException("invalid_name", "The organisation name is required (max 200 characters).");
        }

        if (!Locales.Contains(update.DefaultLocale))
        {
            throw new ValidationFailedException("invalid_locale", "Supported languages are English and Arabic.");
        }

        if (!Calendars.Contains(update.DefaultCalendar))
        {
            throw new ValidationFailedException("invalid_calendar", "Supported calendars are Gregorian and Hijri.");
        }

        if (update.PrimaryColor is not null && !AdminValidation.Color.IsMatch(update.PrimaryColor))
        {
            throw new ValidationFailedException("invalid_color", "The colour must look like #0B5CAD.");
        }

        if (update.LogoAttachmentId is { } logo)
        {
            // The logo must be a scanned-clean image that was uploaded as this tenant's own branding asset.
            var info = await attachments.GetAsync(logo, ct);
            if (info.OwnerType != "Tenant" || info.ScanStatus != ScanStatus.Clean || !info.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                throw new ValidationFailedException("invalid_logo", "The logo must be a clean image uploaded for this organisation.");
            }
        }

        var row = await Load(ct);
        var before = Map(row);
        row.Name = update.Name.Trim();
        row.DefaultLocale = update.DefaultLocale;
        row.DefaultCalendar = update.DefaultCalendar;
        row.PrimaryColor = update.PrimaryColor;
        row.LogoAttachmentId = update.LogoAttachmentId;

        audit.Record(new AuditEntry("tenant.profile.updated", "Tenant", row.Id.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { before, after = Map(row) })));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    private async Task<Domain.Tenant> Load(CancellationToken ct)
    {
        var id = tenant.RequireTenantId();
        return await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException("Tenant");
    }

    private static TenantProfile Map(Domain.Tenant t) =>
        new(t.Id, t.Name, t.Slug, t.DefaultLocale, t.DefaultCalendar, t.PrimaryColor, t.LogoAttachmentId);
}
