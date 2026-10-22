using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Domain;
using Vpms.Workflow.Domain.Errors;

namespace Vpms.Workflow.Infrastructure;

public sealed class WorkflowDefinitionService(
    WorkflowDbContext db, ILegalEntityLookup entities, IApprovalDataLookup approvalData, IApproverDirectory directory,
    ApproverResolverFactory resolvers, IAuditPort audit, ICurrentUser current, TimeProvider clock) : IWorkflowDefinitionService
{
    public async Task<EntityWorkflowDto> GetAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var active = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(d => d.LegalEntityId == legalEntityId && d.SubjectType == SubjectTypes.Invoice && d.Status == DefinitionStatus.Active, ct);
        var pending = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(d => d.LegalEntityId == legalEntityId && d.SubjectType == SubjectTypes.Invoice && d.Status == DefinitionStatus.Pending, ct);
        return new EntityWorkflowDto(active is null ? null : Map(active), pending is null ? null : Map(pending));
    }

    public async Task<IReadOnlyList<DefinitionDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default) =>
        (await db.Definitions.AsNoTracking().Where(d => d.LegalEntityId == legalEntityId).OrderByDescending(d => d.VersionNo).ToListAsync(ct)).Select(Map).ToList();

    public IReadOnlyList<DefinitionIssue> Validate(WorkflowDefinitionSpec spec) =>
        DefinitionValidator.Validate(spec).Select(e => new DefinitionIssue(e.Code, e.Message, e.StepId)).ToList();

    public async Task<DefinitionDto> ProposeAsync(Guid legalEntityId, ProposeDefinition request, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500) throw new ValidationFailedException("reason_required", "Say why the workflow changes (max 500 characters).");

        WorkflowDefinitionSpec spec;
        string source;
        if (request.FromMatrix)
        {
            spec = await PreviewMatrixAsync(legalEntityId, ct);
            source = DefinitionSource.Matrix;
        }
        else
        {
            spec = request.Spec ?? throw new ValidationFailedException("spec_required", "Provide a workflow definition, or choose the approval matrix.");
            source = DefinitionSource.Authored;
        }

        var issues = Validate(spec);
        if (issues.Count > 0) throw new ValidationFailedException("invalid_definition", issues[0].Message);
        await EnsureApproversAllowedAsync(legalEntityId, spec, ct);

        if (await db.Definitions.AnyAsync(d => d.LegalEntityId == legalEntityId && d.SubjectType == SubjectTypes.Invoice && d.Status == DefinitionStatus.Pending, ct))
        {
            throw new ConflictException("change_pending", "There is already a workflow change waiting for approval for this legal entity.");
        }

        var json = DefinitionJson.ToJson(spec);
        var hash = Hash(json);
        var active = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(d => d.LegalEntityId == legalEntityId && d.SubjectType == SubjectTypes.Invoice && d.Status == DefinitionStatus.Active, ct);
        if (active is not null && active.SpecHash == hash) throw new ConflictException("no_change", "This is the workflow already in force.");

        var version = (await db.Definitions.Where(d => d.LegalEntityId == legalEntityId && d.SubjectType == SubjectTypes.Invoice).MaxAsync(d => (int?)d.VersionNo, ct) ?? 0) + 1;
        var row = new WorkflowDefinition
        {
            Id = Guid.NewGuid(), LegalEntityId = legalEntityId, SubjectType = SubjectTypes.Invoice, VersionNo = version, SpecJson = json, SpecHash = hash,
            Source = source, Status = DefinitionStatus.Pending, Note = request.Note.Trim(), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow(),
        };

        try
        {
            db.Definitions.Add(row);
            audit.Record(new AuditEntry("workflow.definition.proposed", "WorkflowDefinition", row.Id.ToString(), legalEntityId,
                JsonSerializer.Serialize(new { version, source, hash })));
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("concurrent_change", "The workflow was changed at the same time. Reload and try again.");
        }

        return Map(row);
    }

    public async Task<IReadOnlyList<DefinitionChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var q = db.Definitions.AsNoTracking().AsQueryable();
        q = string.IsNullOrWhiteSpace(status) ? q.Where(d => d.Status != DefinitionStatus.Superseded) : q.Where(d => d.Status == status);
        if (entityScope is not null) q = q.Where(d => entityScope.Contains(d.LegalEntityId));
        var rows = await q.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(ct);

        var result = new List<DefinitionChangeDto>();
        foreach (var r in rows)
        {
            var baseRow = await db.Definitions.AsNoTracking().Where(b => b.LegalEntityId == r.LegalEntityId && b.SubjectType == r.SubjectType && b.VersionNo < r.VersionNo
                                                                         && (b.Status == DefinitionStatus.Active || b.Status == DefinitionStatus.Superseded))
                .OrderByDescending(b => b.VersionNo).FirstOrDefaultAsync(ct);
            result.Add(new DefinitionChangeDto(Map(r), baseRow is null ? null : Map(baseRow)));
        }

        return result;
    }

    public async Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default) =>
        await db.Definitions.AsNoTracking().Where(d => d.Id == changeId).Select(d => (Guid?)d.LegalEntityId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Workflow change");

    public async Task<DefinitionDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Approve(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.workflow-change.blocked", "WorkflowDefinition", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "approve", rule = ex.Rule })), ct);
            throw;
        }

        // The approvers named in the definition must still be allowed to approve when it goes live.
        await EnsureApproversAllowedAsync(row.LegalEntityId, DefinitionJson.Parse(row.SpecJson), ct);

        // Retire the old version first so the one-active-version index holds at every step.
        var active = await db.Definitions.FirstOrDefaultAsync(d => d.LegalEntityId == row.LegalEntityId && d.SubjectType == row.SubjectType && d.Status == DefinitionStatus.Active && d.Id != row.Id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        row.Status = DefinitionStatus.Pending;
        if (active is not null)
        {
            active.Supersede();
            await db.SaveChangesAsync(ct);
        }

        row.Status = DefinitionStatus.Active;
        audit.Record(new AuditEntry("workflow.definition.approved", "WorkflowDefinition", changeId.ToString(), row.LegalEntityId,
            JsonSerializer.Serialize(new { version = row.VersionNo, hash = row.SpecHash })));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Map(row);
    }

    public async Task<DefinitionDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        try
        {
            row.Reject(current.UserId, comment, clock.GetUtcNow());
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.workflow-change.blocked", "WorkflowDefinition", changeId.ToString(), row.LegalEntityId,
                JsonSerializer.Serialize(new { attempted = "reject", rule = ex.Rule })), ct);
            throw;
        }

        audit.Record(new AuditEntry("workflow.definition.rejected", "WorkflowDefinition", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<DefinitionDto> CancelAsync(Guid changeId, CancellationToken ct = default)
    {
        var row = await Load(changeId, ct);
        row.Cancel(current.UserId, clock.GetUtcNow());
        audit.Record(new AuditEntry("workflow.definition.cancelled", "WorkflowDefinition", changeId.ToString(), row.LegalEntityId));
        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<WorkflowDefinitionSpec> PreviewMatrixAsync(Guid legalEntityId, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var rules = await approvalData.GetMatrixRulesAsync(legalEntityId, ct);
        try
        {
            return MatrixCompiler.Compile("Approval matrix", rules.Select(r => new MatrixRuleInput(r.Name, r.Priority, r.ConditionsJson, r.ApproverResolutionJson, r.Levels)).ToList());
        }
        catch (WorkflowDefinitionException ex)
        {
            throw new ValidationFailedException(ex.Code, ex.Message);
        }
    }

    public async Task<SimulationDto> SimulateAsync(Guid legalEntityId, SimulationRequest request, CancellationToken ct = default)
    {
        await EnsureEntity(legalEntityId, ct);
        var spec = request.Spec ?? (await GetActiveAsync(legalEntityId, SubjectTypes.Invoice, ct))?.Spec;
        if (spec is null) return new SimulationDto("NoWorkflow", [], [], "This legal entity has no active approval workflow.");

        var issues = Validate(spec);
        if (issues.Count > 0) return new SimulationDto("Invalid", [], issues, null);

        var resolver = await ResolverAsync(legalEntityId, spec, ct);
        var facts = new SubjectFacts(request.Amount, (request.Currency ?? "").ToUpperInvariant(), request.VendorId, request.Source ?? "Manual",
            request.Dimensions ?? new Dictionary<string, string>());
        var makers = request.Maker is { } m ? new[] { m } : [];
        try
        {
            var result = WorkflowSimulator.Preview(spec, facts, makers, resolver);
            var route = result.Run.Tasks.Select(t => new SimulatedStep(t.StepId, t.Assignees, t.Mode)).ToList();
            var stalled = result.Run.NeedsAttention || result.Run.Status == RunStatus.Active;
            return new SimulationDto(stalled ? "Stalled" : result.Run.Status, route, [], stalled ? "A step has no eligible approver." : null);
        }
        catch (ConflictException ex) when (ex.Code == "no_route")
        {
            return new SimulationDto("NoRoute", [], [], ex.Message);
        }
        catch (ValidationFailedException ex)
        {
            return new SimulationDto("NoApprover", [], [], ex.Message);
        }
    }

    public async Task<ActiveDefinition?> GetActiveAsync(Guid legalEntityId, string subjectType, CancellationToken ct = default)
    {
        var d = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(x => x.LegalEntityId == legalEntityId && x.SubjectType == subjectType && x.Status == DefinitionStatus.Active, ct);
        return d is null ? null : new ActiveDefinition(d.Id, d.VersionNo, d.SpecHash, DefinitionJson.Parse(d.SpecJson));
    }

    public async Task<ActiveDefinition?> GetVersionAsync(Guid definitionId, CancellationToken ct = default)
    {
        var d = await db.Definitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == definitionId, ct);
        return d is null ? null : new ActiveDefinition(d.Id, d.VersionNo, d.SpecHash, DefinitionJson.Parse(d.SpecJson));
    }

    // --- internals ---------------------------------------------------------------------------------------

    internal Task<ApproverResolver> ResolverAsync(Guid legalEntityId, WorkflowDefinitionSpec spec, CancellationToken ct) => resolvers.CreateAsync(legalEntityId, spec, ct);

    private async Task EnsureApproversAllowedAsync(Guid legalEntityId, WorkflowDefinitionSpec spec, CancellationToken ct)
    {
        var users = ApproverResolverFactory.Approvers(spec).Where(a => a.Kind == ApproverKind.User).Select(a => Guid.Parse(a.Value)).Distinct().ToList();
        if (users.Count == 0) return;
        var permitted = await directory.FilterByPermissionAsync(legalEntityId, Permissions.InvoiceApprove, users, ct);
        var bad = users.FirstOrDefault(u => !permitted.Contains(u));
        if (bad != Guid.Empty)
        {
            throw new ValidationFailedException("approver_not_permitted", "Every named approver must hold the invoice-approval permission for this legal entity.");
        }
    }

    private async Task EnsureEntity(Guid legalEntityId, CancellationToken ct)
    {
        if (!await entities.ExistsAsync(legalEntityId, ct)) throw new NotFoundException("Legal entity");
    }

    private async Task<WorkflowDefinition> Load(Guid id, CancellationToken ct) =>
        await db.Definitions.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException("Workflow change");

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private static DefinitionDto Map(WorkflowDefinition d) => new(d.Id, d.LegalEntityId, d.SubjectType, d.VersionNo, d.Status, d.Source, DefinitionJson.Parse(d.SpecJson),
        d.Note, d.CreatedBy, d.CreatedAt, d.DecidedBy, d.DecidedAt, d.DecisionComment);
}
