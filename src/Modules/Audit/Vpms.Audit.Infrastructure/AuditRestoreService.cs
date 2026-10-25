using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditRestoreService(AuditDbContext db, IAuditChainVerifier verifier, IAuditAnchorSink sink, IAuditPort audit, TenantContext tenant) : IAuditRestoreService
{
    public async Task<ChainVerificationResult> AcceptRestoreAsync(Guid tenantId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationFailedException("reason_required", "A reason is required.");

        // The restored database must be sound on its own; only the comparison with the outside anchors is set aside.
        var result = await verifier.VerifyAsync(tenantId, checkExternalAnchors: false, ct);
        if (!result.IsValid) return result;

        long lastSequence;
        using (tenant.BeginPlatformScope())
        {
            lastSequence = await db.ChainHeads.AsNoTracking().Where(h => h.TenantId == tenantId).Select(h => h.Sequence).FirstOrDefaultAsync(ct);
        }

        await sink.PublishRestoreMarkerAsync(tenantId, lastSequence, reason.Trim(), ct);
        await audit.WriteNowAsync(new AuditEntry("audit.restore.accepted", "AuditChain", tenantId.ToString(),
            ChangeSummaryJson: JsonSerializer.Serialize(new { restoredToSequence = lastSequence, reason = reason.Trim() }), TenantId: tenantId), ct);
        return await verifier.VerifyAsync(tenantId, ct);
    }
}
