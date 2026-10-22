using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Contracts;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

public sealed class MaterialityPolicyService(
    WorkflowDbContext db, ILegalEntityLookup entities, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IMaterialityPolicyService, IMaterialityEvaluator
{
    private const string Subject = SubjectTypes.Invoice;

    public async Task<EntityMaterialityDto> GetAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var active = await Row(legalEntityId, DefinitionStatus.Active, ct);
        var pending = await Row(legalEntityId, DefinitionStatus.Pending, ct);
        return new EntityMaterialityDto(active is null ? Default(legalEntityId) : Map(active), pending is null ? null : Map(pending));
    }

    public async Task<IReadOnlyList<MaterialityDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default) =>
        (await db.MaterialityPolicies.AsNoTracking().Where(p => p.LegalEntityId == legalEntityId).OrderByDescending(p => p.VersionNo).ToListAsync(ct)).Select(Map).ToList();

    public async Task<MaterialityDto> ProposeAsync(Guid legalEntityId, MaterialityConfig config, string note, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        if (string.IsNullOrWhiteSpace(note) || note.Length > 500) throw new ValidationFailedException("reason_required", "Say why the settings change (max 500 characters).");
        var normalised = config with { MaterialFields = config.MaterialFields.Distinct().ToList() };
        var errors = normalised.Validate();
        if (errors.Count > 0) throw new ValidationFailedException(errors[0].Code, errors[0].Message);

        if (await db.MaterialityPolicies.AnyAsync(p => p.LegalEntityId == legalEntityId && p.SubjectType == Subject && p.Status == DefinitionStatus.Pending, ct))
        {
            throw new ConflictException("change_pending", "There is already a change waiting for approval for this legal entity.");
        }

        var json = normalised.ToJson();
        var active = await Row(legalEntityId, DefinitionStatus.Active, ct);
        var inForce = active is null ? MaterialityConfig.Defaults().ToJson() : MaterialityConfig.Parse(active.ConfigJson).ToJson();
        if (json == inForce) throw new ConflictException("no_change", "These are the settings already in force.");

        var version = (await db.MaterialityPolicies.Where(p => p.LegalEntityId == legalEntityId && p.SubjectType == Subject).MaxAsync(p => (int?)p.VersionNo, ct) ?? 0) + 1;
        var row = new MaterialityPolicy
        {
            Id = Guid.NewGuid(), LegalEntityId = legalEntityId, SubjectType = Subject, VersionNo = version, ConfigJson = json, Status = DefinitionStatus.Pending,
            Note = note.Trim(), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };
        try
        {
            db.MaterialityPolicies.Add(row);
            audit.Record(new AuditEntry("materiality.proposed", "MaterialityPolicy", row.Id.ToString(), legalEntityId, JsonSerializer.Serialize(new { version })));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_change", "The settings were changed at the same time. Reload and try again.");
        }

        return Map(row);
    }

    public async Task<IReadOnlyList<MaterialityChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.MaterialityPolicies.AsNoTracking().AsQueryable();
        q = string.IsNullOrWhiteSpace(status) ? q.Where(p => p.Status != DefinitionStatus.Superseded) : q.Where(p => p.Status == status);
        if (entityScope is not null) q = q.Where(p => entityScope.Contains(p.LegalEntityId));
        var rows = await q.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);

        var result = new List<MaterialityChangeDto>();
        foreach (var r in rows)
        {
            var baseRow = await db.MaterialityPolicies.AsNoTracking().Where(b => b.LegalEntityId == r.LegalEntityId && b.VersionNo < r.VersionNo
                    && (b.Status == DefinitionStatus.Active || b.Status == DefinitionStatus.Superseded)).OrderByDescending(b => b.VersionNo).FirstOrDefaultAsync(ct);
            result.Add(new MaterialityChangeDto(Map(r), baseRow is null ? Default(r.LegalEntityId) : Map(baseRow)));
        }

        return result;
    }

    public async Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default) =>
        await db.MaterialityPolicies.AsNoTracking().Where(p => p.Id == changeId).Select(p => (Guid?)p.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Materiality change");

    public async Task<MaterialityDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.materiality-change.blocked", "MaterialityPolicy", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        var active = await db.MaterialityPolicies.FirstOrDefaultAsync(p => p.LegalEntityId == row.LegalEntityId && p.SubjectType == row.SubjectType && p.Status == DefinitionStatus.Active && p.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = DefinitionStatus.Pending;
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = DefinitionStatus.Active;
        audit.Record(new AuditEntry("materiality.approved", "MaterialityPolicy", changeId.ToString(), row.LegalEntityId, JsonSerializer.Serialize(new { version = row.VersionNo })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<MaterialityDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.materiality-change.blocked", "MaterialityPolicy", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("materiality.rejected", "MaterialityPolicy", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<MaterialityDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("materiality.cancelled", "MaterialityPolicy", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<MaterialityResult> EvaluateAsync(Guid legalEntityId, string subjectType, IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> currentSnapshot, CancellationToken ct = default)
    {
        var active = await db.MaterialityPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.LegalEntityId == legalEntityId && p.SubjectType == subjectType && p.Status == DefinitionStatus.Active, ct);
        var config = active is null ? MaterialityConfig.Defaults() : MaterialityConfig.Parse(active.ConfigJson);
        var outcome = Materiality.Evaluate(config, baseline, currentSnapshot);
        return new MaterialityResult(outcome.Material, outcome.ChangedFields);
    }

    // --- internals -----------------------------------------------------------------------------------------

    private Task<MaterialityPolicy?> Row(Guid legalEntityId, string status, CancellationToken ct) =>
        db.MaterialityPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.LegalEntityId == legalEntityId && p.SubjectType == Subject && p.Status == status, ct);

    private async Task<MaterialityPolicy> Load(Guid id, CancellationToken ct) =>
        await db.MaterialityPolicies.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Materiality change");

    private async Task EnsureEntity(Guid legalEntityId, CancellationToken ct)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new NotFoundException("Legal entity");
    }

    private static MaterialityDto Default(Guid legalEntityId) =>
        new(null, legalEntityId, 0, "Default", MaterialityConfig.Defaults(), "Built-in defaults: every change is material", null, null, null, null, null);

    private static MaterialityDto Map(MaterialityPolicy p) =>
        new(p.Id, p.LegalEntityId, p.VersionNo, p.Status, MaterialityConfig.Parse(p.ConfigJson), p.Note, p.CreatedBy, p.CreatedAt, p.DecidedBy, p.DecidedAt, p.DecisionComment);
}
