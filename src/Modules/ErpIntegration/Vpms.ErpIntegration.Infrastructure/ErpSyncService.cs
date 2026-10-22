using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.ErpIntegration.Application;
using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Infrastructure;

public sealed class ErpSyncService(ErpDbContext db, IAuditPort audit, ICurrentUser current, TimeProvider clock, TenantContext tenant) : IErpSyncService, IErpScan
{
    public async Task<SyncSummaryDto> SummaryAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = Scoped(db.SyncRecords.AsNoTracking(), entityScope);
        var counts = await q.GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
        return new SyncSummaryDto(Of(SyncStatus.Pending), Of(SyncStatus.InFlight), Of(SyncStatus.Succeeded), Of(SyncStatus.DeadLetter), Of(SyncStatus.Resolved));
    }

    public async Task<IReadOnlyList<SyncRecordDto>> ListAsync(string? status, string? type, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default)
    {
        var q = Scoped(db.SyncRecords.AsNoTracking(), entityScope);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(type)) q = q.Where(r => r.Type == type);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<SyncRecordDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.SyncRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("ERP message");
        var attempts = await db.SyncAttempts.AsNoTracking().Where(a => a.RecordId == id).OrderBy(a => a.At).ThenBy(a => a.AttemptNo).ToListAsync(ct);
        return new SyncRecordDetailDto(Map(row), attempts.Select(a => new SyncAttemptDto(a.AttemptNo, a.At, a.Outcome, a.Code, a.Message)).ToList());
    }

    public async Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default) =>
        await db.SyncRecords.AsNoTracking().Where(r => r.Id == id).Select(r => (Guid?)r.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("ERP message");

    public async Task<SyncRecordDto> RetryAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var row = await db.SyncRecords.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("ERP message");
        row.Requeue(current.UserId, reason, clock.GetUtcNow());
        audit.Record(new AuditEntry("erp.sync.retried", "ErpSyncRecord", row.Id.ToString(), row.LegalEntityId, JsonSerializer.Serialize(new { row.Type, row.ObjectId, reason = row.HandlingNote })));
        await Save(ct);
        return Map(row);
    }

    public async Task<SyncRecordDto> ResolveAsync(Guid id, string reason, string? erpReference, CancellationToken ct = default)
    {
        var row = await db.SyncRecords.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("ERP message");
        row.ResolveManually(current.UserId, reason, erpReference, clock.GetUtcNow());
        audit.Record(new AuditEntry("erp.sync.resolved-manually", "ErpSyncRecord", row.Id.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { row.Type, row.ObjectId, reason = row.HandlingNote, reference = row.ErpReference })));
        await Save(ct);
        return Map(row);
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithConnectionAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Configs.AsNoTracking().Where(c => c.Kind == ConfigKind.Connection && c.Status == ConfigStatus.Active).Select(c => c.TenantId).Distinct().ToListAsync(ct);
    }

    private async Task Save(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("concurrent_change", "The message was changed at the same time. Reload and try again.");
        }
    }

    private static IQueryable<ErpSyncRecord> Scoped(IQueryable<ErpSyncRecord> q, IReadOnlySet<Guid>? scope) => scope is null ? q : q.Where(r => scope.Contains(r.LegalEntityId));

    private static SyncRecordDto Map(ErpSyncRecord r) => new(
        r.Id, r.LegalEntityId, r.Type, r.ObjectId, r.ObjectVersion, r.Status, r.Attempts, r.NextAttemptAt, r.LastErrorCode, r.LastError, r.ErpReference, r.CreatedAt, r.CompletedAt, r.HandledBy,
        r.HandlingNote);
}
