using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Infrastructure;

public sealed class TenantKeyStore(CustomerAdminDbContext db, IAuditPort audit, TimeProvider clock) : ITenantKeyStore
{
    public async Task<IReadOnlyList<StoredTenantKey>> ListAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.TenantKeys.AsNoTracking().Where(k => k.TenantId == tenantId).OrderBy(k => k.Purpose).ThenBy(k => k.Version)
            .Select(k => new StoredTenantKey(k.TenantId, k.Purpose, k.Version, k.Status, k.KekId, k.WrappedKey))
            .ToListAsync(ct);

    public async Task<bool> TryAddAsync(StoredTenantKey key, int? retireVersion, CancellationToken ct = default)
    {
        if (retireVersion is { } old)
        {
            var current = await db.TenantKeys.FirstOrDefaultAsync(
                k => k.TenantId == key.TenantId && k.Purpose == key.Purpose && k.Version == old, ct);
            if (current is not null)
            {
                current.Status = KeyStatus.Retired;
            }
        }

        db.TenantKeys.Add(new TenantKey
        {
            Id = Guid.NewGuid(), TenantId = key.TenantId, Purpose = key.Purpose, Version = key.Version, Status = key.Status,
            KekId = key.KekId, WrappedKey = key.WrappedKey, CreatedAt = clock.GetUtcNow(),
        });
        // Only metadata is audited: never the key material, wrapped or not.
        audit.Record(new AuditEntry(key.Version == 1 ? "encryption.key.created" : "encryption.key.rotated", "TenantKey",
            $"{key.Purpose}:{key.Version}", TenantId: key.TenantId, ActorType: AuditActorType.System,
            ChangeSummaryJson: $"{{\"purpose\":\"{key.Purpose}\",\"version\":{key.Version},\"kek\":\"{key.KekId}\"}}"));

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
