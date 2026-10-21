using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditPort(
    AuditBuffer buffer,
    AuditContext context,
    TenantContext tenant,
    AuditDbContext db,
    IAuditAppender appender) : IAuditPort
{
    public void Record(AuditEntry entry) => buffer.Add(Resolve(entry));

    public async Task WriteNowAsync(AuditEntry entry, CancellationToken ct = default)
    {
        var resolved = Resolve(entry);
        // The tenant may not be established yet (e.g. a denied sign-in), so write as the platform for this one insert.
        using var _ = tenant.BeginPlatformScope();
        // Open through EF (not the raw connection) so the tenant/platform session context is applied for RLS.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await appender.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), [resolved], ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private ResolvedAuditEntry Resolve(AuditEntry e)
    {
        var tenantId = e.TenantId ?? tenant.TenantId
                       ?? throw new InvalidOperationException("Audit entry has no tenant.");
        var actorId = e.ActorId ?? context.ActorId;
        var actorType = e.ActorType ?? context.ActorType ?? (actorId is null ? AuditActorType.System : AuditActorType.User);
        return new ResolvedAuditEntry(tenantId, e.LegalEntityId, actorType, actorId, e.OnBehalfOfId ?? context.OnBehalfOfId,
            e.Action, e.EntityType, e.EntityId, context.CorrelationId, context.IpAddress,
            e.BeforeHash, e.AfterHash, e.ChangeSummaryJson);
    }
}
