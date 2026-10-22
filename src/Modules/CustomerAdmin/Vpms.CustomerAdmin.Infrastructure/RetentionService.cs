using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Jobs;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Application;
using Vpms.CustomerAdmin.Contracts;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Infrastructure;

/// <summary>The tenant's retention policy: versioned, each version approved by a different person (ADR-0024).</summary>
public sealed class RetentionPolicyService(CustomerAdminDbContext db, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IRetentionPolicyService, IRetentionPolicyReader
{
    public async Task<RetentionStateDto> GetAsync(CancellationToken ct = default)
    {
        var active = await db.RetentionPolicies.AsNoTracking().Where(p => p.Status == RetentionStatus.Active).FirstOrDefaultAsync(ct);
        var pending = await db.RetentionPolicies.AsNoTracking().Where(p => p.Status == RetentionStatus.Pending).FirstOrDefaultAsync(ct);
        return new RetentionStateDto(active is null ? null : Map(active), pending is null ? null : Map(pending), RetentionDefaults.Months, RetentionClasses.Disposed);
    }

    public async Task<RetentionPolicyDto> ProposeAsync(RetentionSettings settings, string note, CancellationToken ct = default)
    {
        var issues = settings.Validate();
        if (issues.Count > 0) throw new ValidationFailedException(issues[0].Code, issues[0].Message);
        if (string.IsNullOrWhiteSpace(note)) throw new ValidationFailedException("reason_required", "Say why the policy is changing.");
        if (await db.RetentionPolicies.AnyAsync(p => p.Status == RetentionStatus.Pending, ct))
        {
            throw new ConflictException("change_pending", "A change is already waiting for approval. Cancel it or have it decided first.");
        }

        var next = (await db.RetentionPolicies.MaxAsync(p => (int?)p.VersionNo, ct) ?? 0) + 1;
        var row = new RetentionPolicyVersion
        {
            Id = Guid.NewGuid(), VersionNo = next, SettingsJson = settings.ToJson(), Status = RetentionStatus.Pending, Note = note.Trim()[..Math.Min(note.Trim().Length, 500)],
            CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };
        db.RetentionPolicies.Add(row);
        audit.Record(new AuditEntry("retention.policy.proposed", "RetentionPolicy", row.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { version = next })));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<RetentionPolicyDto> ApproveAsync(Guid id, string? comment, CancellationToken ct = default)
    {
        var row = await Load(id, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.retention-policy.blocked", "RetentionPolicy", id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        // Retire the old version first so there is never more than one active version.
        var active = await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Status == RetentionStatus.Active && p.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = RetentionStatus.Pending;
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = RetentionStatus.Active;
        audit.Record(new AuditEntry("retention.policy.approved", "RetentionPolicy", id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { version = row.VersionNo, settings = JsonDocument.Parse(row.SettingsJson).RootElement })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<RetentionPolicyDto> RejectAsync(Guid id, string comment, CancellationToken ct = default)
    {
        var row = await Load(id, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.retention-policy.blocked", "RetentionPolicy", id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("retention.policy.rejected", "RetentionPolicy", id.ToString()));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<RetentionPolicyDto> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var row = await Load(id, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("retention.policy.cancelled", "RetentionPolicy", id.ToString()));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    /// <summary>The active policy and holds as a context for one retention pass.</summary>
    public async Task<RetentionContext> ContextAsync(CancellationToken ct = default)
    {
        var active = await db.RetentionPolicies.AsNoTracking().Where(p => p.Status == RetentionStatus.Active).Select(p => p.SettingsJson).FirstOrDefaultAsync(ct);
        var settings = active is null ? RetentionSettings.Default : RetentionSettings.Parse(active);
        var holds = await db.LegalHolds.AsNoTracking().Where(h => h.ReleasedAt == null).ToListAsync(ct);
        return new RetentionContext(
            clock.GetUtcNow(), settings.MonthsFor,
            holds.Any(h => h.Scope == HoldScope.Tenant),
            holds.Where(h => h.LegalEntityId != null).Select(h => h.LegalEntityId!.Value).ToHashSet(),
            holds.Where(h => h.VendorId != null).Select(h => h.VendorId!.Value).ToHashSet());
    }

    private async Task<RetentionPolicyVersion> Load(Guid id, CancellationToken ct) =>
        await db.RetentionPolicies.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Retention policy change");

    private static RetentionPolicyDto Map(RetentionPolicyVersion r) =>
        new(r.Id, r.VersionNo, r.Status, RetentionSettings.Parse(r.SettingsJson), r.Note, r.CreatedBy, r.CreatedAt, r.DecidedBy, r.DecidedAt, r.DecisionComment);
}

public sealed class LegalHoldService(CustomerAdminDbContext db, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : ILegalHoldService
{
    public async Task<IReadOnlyList<LegalHoldDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        var q = db.LegalHolds.AsNoTracking().AsQueryable();
        if (activeOnly) q = q.Where(h => h.ReleasedAt == null);
        return (await q.OrderByDescending(h => h.PlacedAt).Take(500).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<LegalHoldDto> PlaceAsync(string scope, Guid? legalEntityId, Guid? vendorId, string reason, CancellationToken ct = default)
    {
        var hold = LegalHold.Place(scope, legalEntityId, vendorId, reason, current.UserId, clock.GetUtcNow());
        if (legalEntityId is { } entity && !await entities.ExistsAsync(entity, ct)) throw new ValidationFailedException("unknown_legal_entity", "The legal entity does not exist.");
        db.LegalHolds.Add(hold);
        audit.Record(new AuditEntry("retention.hold.placed", "LegalHold", hold.Id.ToString(), legalEntityId, JsonSerializer.Serialize(new { scope, vendorId })));
        await db.SaveChangesAsync(ct);
        return Map(hold);
    }

    public async Task<LegalHoldDto> ReleaseAsync(Guid id, string comment, CancellationToken ct = default)
    {
        var hold = await db.LegalHolds.FirstOrDefaultAsync(h => h.Id == id, ct) ?? throw new NotFoundException("Legal hold");
        try
        {
            hold.Release(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.legal-hold.blocked", "LegalHold", id.ToString(), hold.LegalEntityId, JsonSerializer.Serialize(new { attempted = "release", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("retention.hold.released", "LegalHold", id.ToString(), hold.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(hold);
    }

    private static LegalHoldDto Map(LegalHold h) => new(h.Id, h.Scope, h.LegalEntityId, h.VendorId, h.Reason, h.PlacedBy, h.PlacedAt, h.ReleasedBy, h.ReleasedAt, h.ReleaseComment);
}

/// <summary>What has passed its retention period, by class. Counts only; the report deletes nothing.</summary>
public sealed class RetentionReportService(IRetentionPolicyReader policy, IEnumerable<IRetentionTarget> targets) : IRetentionReportService
{
    public async Task<IReadOnlyList<RetentionReportRow>> BuildAsync(CancellationToken ct = default)
    {
        var context = await policy.ContextAsync(ct);
        var rows = new List<RetentionReportRow>();
        foreach (var cls in RetentionClasses.All)
        {
            var target = targets.FirstOrDefault(t => t.DataClass == cls);
            int? eligible = target is null ? null : await target.CountEligibleAsync(context, ct);
            rows.Add(new RetentionReportRow(cls, context.MonthsFor(cls), context.CutoffFor(cls), target?.CanDispose == true, eligible, context.TenantHeld));
        }

        return rows;
    }
}
