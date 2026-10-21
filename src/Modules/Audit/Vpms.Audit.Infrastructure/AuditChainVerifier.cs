using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditChainVerifier(AuditDbContext db, TenantContext tenant) : IAuditChainVerifier
{
    public async Task<ChainVerificationResult> VerifyAsync(Guid tenantId, CancellationToken ct = default)
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

        foreach (var anchor in await db.Anchors.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(ct))
        {
            var ev = await db.Events.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Sequence == anchor.Sequence)
                .Select(x => x.Hash).FirstOrDefaultAsync(ct);
            if (ev != anchor.Hash)
            {
                return ChainVerificationResult.Broken(checkedCount, anchor.Sequence, "Stored event does not match the externally anchored hash.");
            }
        }

        return ChainVerificationResult.Ok(checkedCount);
    }

    public async Task<IReadOnlyList<Guid>> ListTenantsWithEventsAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.ChainHeads.AsNoTracking().Select(h => h.TenantId).ToListAsync(ct);
    }
}
