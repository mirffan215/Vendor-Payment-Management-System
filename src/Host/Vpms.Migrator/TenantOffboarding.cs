using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.Migrator;

public sealed record ShredResult(int FilesDeleted, int KeysDestroyed);

/// <summary>
/// Operator commands for a customer leaving the service (ADR-0024): suspend (reversible), resume, and shred (final). They run with the owner
/// identity from the migrator, never from the web app, and each step is written to the tenant's audit trail.
/// </summary>
public static class TenantOffboarding
{
    public static Task SuspendAsync(string cs, string slug, string reason, CancellationToken ct = default) =>
        ChangeStatusAsync(cs, slug, TenantStatus.Suspended, [TenantStatus.Active], "tenant.suspended", reason, ct);

    public static Task ResumeAsync(string cs, string slug, string reason, CancellationToken ct = default) =>
        ChangeStatusAsync(cs, slug, TenantStatus.Active, [TenantStatus.Suspended], "tenant.resumed", reason, ct);

    /// <summary>
    /// Destroys the tenant's data keys, deletes its files by tenant prefix and marks it offboarded. Refused unless the tenant has been suspended for
    /// at least <paramref name="minDays"/> days, has no active legal hold, and the caller repeats the slug.
    /// </summary>
    public static async Task<ShredResult> ShredAsync(string cs, string slug, string confirmSlug, int minDays, IBlobBackend blobs, TimeProvider clock, CancellationToken ct = default)
    {
        if (!string.Equals(slug, confirmSlug, StringComparison.Ordinal)) throw new ArgumentException("--confirm must repeat the tenant slug exactly.");
        var tenant = new TenantContext();
        using var platform = tenant.BeginPlatformScope();
        await using var customer = Create<CustomerAdminDbContext>(cs, tenant, o => new CustomerAdminDbContext(o, tenant));

        var row = await customer.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct) ?? throw new InvalidOperationException($"No tenant with slug '{slug}'.");
        if (row.Status != TenantStatus.Suspended) throw new InvalidOperationException($"The tenant is {row.Status}; only a suspended tenant can be shredded.");
        var suspendedFor = clock.GetUtcNow() - (row.StatusChangedAt ?? DateTimeOffset.MaxValue);
        if (suspendedFor < TimeSpan.FromDays(minDays)) throw new InvalidOperationException($"The cooling-off period is {minDays} days; the tenant has been suspended for {suspendedFor.TotalDays:0.#}.");
        if (await customer.LegalHolds.IgnoreQueryFilters().AnyAsync(h => h.TenantId == row.Id && h.ReleasedAt == null, ct))
        {
            throw new InvalidOperationException("The tenant has an active legal hold; release it first.");
        }

        // The audit trail is the tenant's own, so it is written in that tenant's context, separately from the platform-wide work below.
        var auditTenant = new TenantContext();
        auditTenant.SetTenant(row.Id);
        await using var auditDb = Create<AuditDbContext>(cs, auditTenant, o => new AuditDbContext(o, auditTenant));
        var port = Port(auditDb, auditTenant, "shred-tenant");
        await port.WriteNowAsync(new AuditEntry("tenant.shred.started", "Tenant", row.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { slug, suspendedDays = (int)suspendedFor.TotalDays }), TenantId: row.Id), ct);

        // Files first, then the keys, then the status: a failure part-way leaves a suspended tenant that can be shredded again.
        var files = await blobs.DeletePrefixAsync(TenantStoragePath.PrefixFor(row.Id), ct);
        var keys = await customer.TenantKeys.IgnoreQueryFilters().Where(k => k.TenantId == row.Id && k.Status != KeyStatus.Destroyed)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.Status, KeyStatus.Destroyed).SetProperty(k => k.WrappedKey, Array.Empty<byte>()), ct);

        row.Status = TenantStatus.Offboarded;
        row.StatusChangedAt = clock.GetUtcNow();
        await customer.SaveChangesAsync(ct);
        await port.WriteNowAsync(new AuditEntry("tenant.offboarded", "Tenant", row.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { filesDeleted = files, keysDestroyed = keys }), TenantId: row.Id), ct);
        return new ShredResult(files, keys);
    }

    private static async Task ChangeStatusAsync(string cs, string slug, string to, string[] from, string action, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("--reason is required.");
        var tenant = new TenantContext();
        using var platform = tenant.BeginPlatformScope();
        await using var customer = Create<CustomerAdminDbContext>(cs, tenant, o => new CustomerAdminDbContext(o, tenant));
        await using var auditDb = Create<AuditDbContext>(cs, tenant, o => new AuditDbContext(o, tenant));
        var row = await customer.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct) ?? throw new InvalidOperationException($"No tenant with slug '{slug}'.");
        if (!from.Contains(row.Status)) throw new InvalidOperationException($"The tenant is {row.Status}; expected {string.Join(" or ", from)}.");
        row.Status = to;
        row.StatusChangedAt = DateTimeOffset.UtcNow;
        await customer.SaveChangesAsync(ct);
        await Port(auditDb, tenant, action).WriteNowAsync(new AuditEntry(action, "Tenant", row.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { slug, reason = reason.Trim() }), TenantId: row.Id), ct);
    }

    private static AuditPort Port(AuditDbContext db, TenantContext tenant, string correlation) =>
        new(new AuditBuffer(), new AuditContext { ActorType = AuditActorType.System, CorrelationId = correlation }, tenant, db, new SqlAuditAppender(TimeProvider.System));

    private static T Create<T>(string cs, TenantContext tenant, Func<DbContextOptions<T>, T> factory) where T : DbContext =>
        factory(new DbContextOptionsBuilder<T>().UseSqlServer(cs).AddInterceptors(new TenantSessionContextInterceptor(tenant)).Options);
}
