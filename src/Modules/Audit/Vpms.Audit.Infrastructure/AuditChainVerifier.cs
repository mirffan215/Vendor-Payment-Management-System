using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditChainVerifier(AuditDbContext db, TenantContext tenant, IAuditAnchorSink? sink = null) : IAuditChainVerifier
{
    public Task<ChainVerificationResult> VerifyAsync(Guid tenantId, CancellationToken ct = default) => VerifyAsync(tenantId, true, ct);

    public async Task<ChainVerificationResult> VerifyAsync(Guid tenantId, bool checkExternalAnchors, CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();

        long expected = 1;
        var prevHash = AuditHasher.GenesisHash;
        var checkedCount = 0L;

        await foreach (var e in db.Events.AsNoTracking().Where(x => x.TenantId == tenantId)
                           .OrderBy(x => x.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (e.Sequence != expected)
            {
                return ChainVerificationResult.Broken(checkedCount, expected, $"Missing or out-of-order event: expected sequence {expected}, found {e.Sequence}.");
            }

            if (e.PrevHash != prevHash)
            {
                return ChainVerificationResult.Broken(checkedCount, e.Sequence, "Previous-hash link is broken.");
            }

            if (AuditHasher.Compute(prevHash, e) != e.Hash)
            {
                return ChainVerificationResult.Broken(checkedCount, e.Sequence, "Event content does not match its hash.");
            }

            prevHash = e.Hash;
            expected++;
            checkedCount++;
        }

        var head = await db.ChainHeads.AsNoTracking().FirstOrDefaultAsync(h => h.TenantId == tenantId, ct);
        var lastSequence = expected - 1;
        if (head is null ? lastSequence != 0 : head.Sequence != lastSequence || head.Hash != prevHash)
        {
            return ChainVerificationResult.Broken(checkedCount, lastSequence + 1, "Chain head does not match the stored events (events removed or head altered).");
        }

        // One query for all anchored events instead of one per anchor (anchors accumulate hourly).
        var anchors = await db.Anchors.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(ct);
        var sequences = anchors.Select(a => a.Sequence).ToList();
        var hashes = await db.Events.AsNoTracking().Where(x => x.TenantId == tenantId && sequences.Contains(x.Sequence)).ToDictionaryAsync(x => x.Sequence, x => x.Hash, ct);
        foreach (var anchor in anchors)
        {
            if (hashes.GetValueOrDefault(anchor.Sequence) != anchor.Hash)
            {
                return ChainVerificationResult.Broken(checkedCount, anchor.Sequence, "Stored event does not match the externally anchored hash.");
            }
        }

        if (checkExternalAnchors && sink is not null)
        {
            var problem = await CheckExternalAnchorsAsync(tenantId, expected - 1, checkedCount, ct);
            if (problem is not null) return problem;
        }

        return ChainVerificationResult.Ok(checkedCount);
    }

    /// <summary>
    /// The anchors published outside the database are what a restore from an older backup (or a rewritten database) is measured against. A restore marker
    /// accepted by a person retires the anchors published earlier for sequences after the point it restored to.
    /// </summary>
    private async Task<ChainVerificationResult?> CheckExternalAnchorsAsync(Guid tenantId, long lastSequence, long checkedCount, CancellationToken ct)
    {
        var effective = new List<ExternalAnchor>();
        foreach (var item in await sink!.ListAsync(tenantId, ct))
        {
            if (item.Kind == ExternalAnchorKind.Restore) effective.RemoveAll(a => a.Sequence > item.Sequence);
            else effective.Add(item);
        }

        foreach (var anchor in effective)
        {
            if (anchor.Sequence > lastSequence)
            {
                return ChainVerificationResult.Broken(checkedCount, lastSequence + 1,
                    $"Events are missing: the trail was anchored outside the database up to sequence {anchor.Sequence}, but the database ends at {lastSequence}. Was it restored from an older backup?");
            }

            var stored = await db.Events.AsNoTracking().Where(x => x.TenantId == tenantId && x.Sequence == anchor.Sequence).Select(x => x.Hash).FirstOrDefaultAsync(ct);
            if (stored != anchor.Hash)
            {
                return ChainVerificationResult.Broken(checkedCount, anchor.Sequence, "Stored event does not match the anchor published outside the database.");
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithEventsAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.ChainHeads.AsNoTracking().Select(h => h.TenantId).ToListAsync(ct);
    }
}
