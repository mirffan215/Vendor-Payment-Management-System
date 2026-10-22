using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Identity.Contracts;
using Vpms.Workflow.Application;
using Vpms.Workflow.Contracts;
using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Infrastructure;

public sealed class ApprovalService(
    WorkflowDbContext db, WorkflowDefinitionService definitions, ApproverResolverFactory resolvers, IApprovalDataLookup approvalData,
    IApproverDirectory directory, IUserDirectory users, IEnumerable<IApprovalOutcomeHandler> handlers, TenantContext tenant,
    IAuditPort audit, ICurrentUser current, TimeProvider clock, ILogger<ApprovalService> logger) : IApprovalService, IApprovalScan, IApprovalLifecycle
{
    // --- starting ------------------------------------------------------------------------------------------

    public async Task<ApprovalStarted> StartAsync(ApprovalSubject subject, CancellationToken ct = default)
    {
        var existing = await db.Runs.Include(r => r.Tasks).FirstOrDefaultAsync(r => r.SubjectType == subject.SubjectType && r.SubjectId == subject.SubjectId && r.Status == RunStatus.Active, ct);
        if (existing is not null)
        {
            if (existing.SubjectVersion == subject.SubjectVersion) return Started(existing);

            // The subject module only starts an approval for a version it has verified, so a run for an older version is obsolete.
            await CancelAsync(existing, "Superseded by a newer version.", ct);
        }

        var definition = await definitions.GetActiveAsync(subject.LegalEntityId, subject.SubjectType, ct)
            ?? throw new ConflictException("no_workflow", "This legal entity has no approval workflow yet. An administrator must set one up and have it approved.");

        var resolver = await resolvers.CreateAsync(subject.LegalEntityId, definition.Spec, ct);
        var now = clock.GetUtcNow();
        var facts = new SubjectFacts(subject.Amount, subject.Currency.ToUpperInvariant(), subject.VendorId, subject.Source, subject.Dimensions);
        var run = WorkflowRun.Start(definition.Spec, facts, subject.Makers.Append(current.UserId), current.UserId, resolver, now);
        run.LegalEntityId = subject.LegalEntityId;
        run.SubjectType = subject.SubjectType;
        run.SubjectId = subject.SubjectId;
        run.SubjectVersion = subject.SubjectVersion;
        run.SubjectLabel = subject.Label.Length > 200 ? subject.Label[..200] : subject.Label;
        run.DefinitionId = definition.Id;
        run.DefinitionVersion = definition.VersionNo;
        run.DefinitionHash = definition.Hash;

        db.Runs.Add(run);
        audit.Record(new AuditEntry("approval.started", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
            JsonSerializer.Serialize(new { subject.SubjectType, subject.SubjectId, subject.SubjectVersion, definitionVersion = definition.VersionNo, tasks = run.Tasks.Select(t => t.StepId) })));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests started the same approval at once; the unique index let one through.
            throw new ConflictException("approval_in_progress", "An approval is already running for this item.");
        }

        return Started(run);
    }

    // --- lifecycle (called by subject modules) ----------------------------------------------------------

    public async Task CancelActiveAsync(string subjectType, Guid subjectId, string reason, CancellationToken ct = default)
    {
        var run = await db.Runs.Include(r => r.Tasks).FirstOrDefaultAsync(r => r.SubjectType == subjectType && r.SubjectId == subjectId && r.Status == RunStatus.Active, ct);
        if (run is not null) await CancelAsync(run, reason, ct);
    }

    public async Task RebindAsync(string subjectType, Guid subjectId, int newVersion, decimal newAmount, Guid editor, CancellationToken ct = default)
    {
        var run = await db.Runs.Include(r => r.Tasks).FirstOrDefaultAsync(r => r.SubjectType == subjectType && r.SubjectId == subjectId && r.Status == RunStatus.Active, ct);
        if (run is null) return;
        run.Rebind(newVersion, newAmount, editor);
        audit.Record(new AuditEntry("approval.rebound", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
            System.Text.Json.JsonSerializer.Serialize(new { newVersion, newAmount, editor })));
        await db.SaveChangesAsync(ct);
    }

    private async Task CancelAsync(WorkflowRun run, string reason, CancellationToken ct)
    {
        run.Cancel(reason, clock.GetUtcNow());
        run.OutcomeAppliedAt = clock.GetUtcNow(); // nothing to tell the subject: it is the one that cancelled
        audit.Record(new AuditEntry("approval.cancelled", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
            System.Text.Json.JsonSerializer.Serialize(new { run.SubjectType, run.SubjectId, run.SubjectVersion, reason })));
        await db.SaveChangesAsync(ct);
    }

    // --- reading -------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<InboxItemDto>> InboxAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var me = current.UserId;
        var now = clock.GetUtcNow();

        // Tasks assigned to me, and tasks assigned to someone who has delegated to me for that legal entity (within any amount cap).
        var received = await db.Delegations.AsNoTracking()
            .Where(d => d.DelegateId == me && d.Status == DelegationStatus.Active && d.ValidFrom <= now && now < d.ValidTo).ToListAsync(ct);
        var sources = new List<(Guid Owner, Guid? ActingFor, Delegation? By)> { (me, null, null) };
        sources.AddRange(received.Select(d => (d.DelegatorId, (Guid?)d.DelegatorId, (Delegation?)d)));

        var items = new Dictionary<Guid, (ApprovalTask T, WorkflowRun R, Guid? ActingFor)>();
        foreach (var (owner, actingFor, by) in sources)
        {
            var needle = owner.ToString();
            var query =
                from t in db.Tasks.AsNoTracking()
                join r in db.Runs.AsNoTracking() on t.RunId equals r.Id
                where t.Status == Domain.TaskStatus.Pending && r.Status == RunStatus.Active && t.AssigneeList.Contains(needle)
                      && !db.Decisions.Any(d => d.TaskId == t.Id && (d.ActorId == owner || d.ActingForId == owner || d.ActorId == me || d.ActingForId == me))
                select new { t, r };
            if (entityScope is not null) query = query.Where(x => entityScope.Contains(x.r.LegalEntityId));
            if (by is not null) query = query.Where(x => x.r.LegalEntityId == by.LegalEntityId);
            foreach (var x in await query.Take(200).ToListAsync(ct))
            {
                if (by?.MaxAmount is { } cap && x.r.Facts.Amount > cap) continue;
                if (x.r.Makers.Contains(me)) continue; // a delegate who prepared the invoice can never decide it
                items.TryAdd(x.t.Id, (x.t, x.r, actingFor));
            }
        }

        var rows = items.Values.OrderBy(x => x.T.DueAt ?? DateTimeOffset.MaxValue).ThenBy(x => x.T.CreatedAt).Take(200).ToList();
        var taskIds = rows.Select(x => x.T.Id).ToList();
        var approvals = await db.Decisions.AsNoTracking().Where(d => taskIds.Contains(d.TaskId) && d.Decision == DecisionKind.Approve)
            .GroupBy(d => d.TaskId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var names = await users.GetAsync(rows.Where(x => x.ActingFor is not null).Select(x => x.ActingFor!.Value).Distinct(), ct);

        return rows.Select(x => new InboxItemDto(x.T.Id, x.R.Id, x.T.StepId, x.R.SubjectType, x.R.SubjectId, x.R.SubjectLabel, x.R.LegalEntityId,
            x.R.Facts.Amount, x.R.Facts.Currency, x.T.Mode, approvals.GetValueOrDefault(x.T.Id), x.T.Assignees.Count, x.T.CreatedAt, x.T.DueAt,
            x.T.DueAt is { } due && due <= now, x.T.EscalatedAt is not null, x.ActingFor,
            x.ActingFor is { } a && names.TryGetValue(a, out var u) ? u.DisplayName : null)).ToList();
    }

    public async Task<Guid> EntityOfTaskAsync(Guid taskId, CancellationToken ct = default) =>
        await (from t in db.Tasks.AsNoTracking() join r in db.Runs.AsNoTracking() on t.RunId equals r.Id where t.Id == taskId select (Guid?)r.LegalEntityId).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Approval task");

    public async Task<RunDto> GetRunOfTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var runId = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.RunId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Approval task");
        return (await MapRunsAsync(db.Runs.AsNoTracking().Where(r => r.Id == runId), ct)).Single();
    }

    public async Task<IReadOnlyList<RunDto>> RunsForSubjectAsync(string subjectType, Guid subjectId, CancellationToken ct = default) =>
        await MapRunsAsync(db.Runs.AsNoTracking().Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId), ct);

    // --- deciding ------------------------------------------------------------------------------------------

    public async Task<RunDto> DecideAsync(Guid taskId, DecideRequest request, CancellationToken ct = default)
    {
        var runId = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => (Guid?)t.RunId).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Approval task");
        var run = await db.Runs.Include(r => r.Tasks).ThenInclude(t => t.Decisions).Include(r => r.Joins).FirstAsync(r => r.Id == runId, ct);
        var definition = await definitions.GetVersionAsync(run.DefinitionId, ct) ?? throw new NotFoundException("Workflow version");

        var actor = current.UserId;
        var effective = request.ActingFor ?? actor;
        var now = clock.GetUtcNow();
        var wasActive = run.Status == RunStatus.Active;
        try
        {
            // Maker-checker and different-person run before anything else, so a refused attempt reveals nothing and is audited.
            run.EnsureIndependent(actor, effective);
            if (effective != actor) await EnsureDelegationAsync(run, actor, effective, now, ct);
            await EnsureMayApproveAsync(run.LegalEntityId, actor, effective, ct);

            var authority = request.Decision != DecisionKind.Approve
                || await approvalData.HasAuthorityAsync(run.LegalEntityId, effective, run.Facts.Amount, run.Facts.Currency, run.Facts.Dimensions, now, ct);
            var resolver = await resolvers.CreateAsync(run.LegalEntityId, definition.Spec, ct);
            run.Decide(definition.Spec, taskId, new DecisionRequest(actor, request.Decision, request.Comment, now, request.ActingFor, _ => authority), resolver);
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.invoice-approval.blocked", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
                JsonSerializer.Serialize(new { taskId, attempted = request.Decision, rule = ex.Rule, actingFor = request.ActingFor })), ct);
            throw;
        }

        audit.Record(new AuditEntry($"approval.{request.Decision.ToLowerInvariant()}", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
            JsonSerializer.Serialize(new { taskId, step = run.Tasks.First(t => t.Id == taskId).StepId, actingFor = request.ActingFor, run.SubjectType, run.SubjectId })));
        if (wasActive && run.Status != RunStatus.Active)
        {
            audit.Record(new AuditEntry($"approval.{run.Status.ToLowerInvariant()}", "WorkflowRun", run.Id.ToString(), run.LegalEntityId,
                JsonSerializer.Serialize(new { run.SubjectType, run.SubjectId, run.SubjectVersion })));
        }

        await db.SaveChangesAsync(ct);
        if (run.Status != RunStatus.Active) await ApplyOutcomeAsync(run, ct);
        return (await MapRunsAsync(db.Runs.AsNoTracking().Where(r => r.Id == run.Id), ct)).Single();
    }

    private async Task EnsureDelegationAsync(WorkflowRun run, Guid delegateId, Guid delegatorId, DateTimeOffset now, CancellationToken ct)
    {
        var amount = run.Facts.Amount;
        var ok = await db.Delegations.AsNoTracking().AnyAsync(d => d.DelegatorId == delegatorId && d.DelegateId == delegateId && d.LegalEntityId == run.LegalEntityId
            && d.Status == DelegationStatus.Active && d.ValidFrom <= now && now < d.ValidTo && (d.MaxAmount == null || d.MaxAmount >= amount), ct);
        if (!ok) throw new ValidationFailedException("no_delegation", "You have no delegation from that approver that covers this approval.", 403);
    }

    private async Task EnsureMayApproveAsync(Guid legalEntityId, Guid actor, Guid effective, CancellationToken ct)
    {
        var ids = new[] { actor, effective }.Distinct().ToList();
        var allowed = await directory.FilterByPermissionAsync(legalEntityId, Permissions.InvoiceApprove, ids, ct);
        if (ids.Any(i => !allowed.Contains(i)))
        {
            throw new ValidationFailedException("not_an_approver", "You do not hold the approval permission for this legal entity.", 403);
        }
    }

    // --- outcomes ------------------------------------------------------------------------------------------

    public async Task<int> ApplyPendingOutcomesAsync(CancellationToken ct = default)
    {
        var runs = await db.Runs.Where(r => r.Status != RunStatus.Active && r.OutcomeAppliedAt == null).OrderBy(r => r.CompletedAt).Take(100).ToListAsync(ct);
        var applied = 0;
        foreach (var run in runs)
        {
            if (await ApplyOutcomeAsync(run, ct)) applied++;
        }

        return applied;
    }

    public async Task<IReadOnlyList<Guid>> TenantsWithPendingOutcomesAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginPlatformScope();
        return await db.Runs.AsNoTracking().Where(r => r.Status != RunStatus.Active && r.OutcomeAppliedAt == null).Select(r => r.TenantId).Distinct().ToListAsync(ct);
    }

    private async Task<bool> ApplyOutcomeAsync(WorkflowRun run, CancellationToken ct)
    {
        var handler = handlers.FirstOrDefault(h => h.SubjectType == run.SubjectType);
        if (handler is null)
        {
            logger.LogWarning("No outcome handler for subject type {SubjectType}.", run.SubjectType);
            return false;
        }

        try
        {
            await handler.ApplyAsync(new ApprovalOutcome(run.SubjectType, run.SubjectId, run.SubjectVersion, run.Id, run.Status, run.StatusReason, run.CompletedAt ?? clock.GetUtcNow()), ct);
            run.OutcomeAppliedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The decision is already stored. The sweep tries again; the handler is idempotent.
            logger.LogError(ex, "Applying approval outcome {Outcome} of run {RunId} failed; it will be retried.", run.Status, run.Id);
            return false;
        }
    }

    // --- mapping -------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<RunDto>> MapRunsAsync(IQueryable<WorkflowRun> query, CancellationToken ct)
    {
        var runs = await query.Include(r => r.Tasks).ThenInclude(t => t.Decisions).OrderByDescending(r => r.StartedAt).ToListAsync(ct);
        var ids = runs.SelectMany(r => r.Tasks).SelectMany(t => t.Assignees.Concat(t.Decisions.SelectMany(d => new[] { d.ActorId, d.ActingForId ?? d.ActorId }))).Distinct().ToList();
        var names = await users.GetAsync(ids, ct);
        string? Name(Guid? id) => id is { } g && names.TryGetValue(g, out var u) ? u.DisplayName : null;

        return runs.Select(r => new RunDto(r.Id, r.SubjectType, r.SubjectId, r.SubjectVersion, r.SubjectLabel, r.LegalEntityId, r.Status, r.StatusReason,
            r.DefinitionVersion, r.StartedAt, r.CompletedAt, r.Facts.Amount, r.Facts.Currency, r.NeedsAttention,
            r.Tasks.OrderBy(t => t.CreatedAt).ThenBy(t => t.StepId).Select(t => new TaskDto(t.Id, t.StepId, t.BranchPath, t.Status, t.Mode, t.Quorum, t.Assignees, t.Assignees.Select(a => Name(a)).ToList(),
                t.CreatedAt, t.DueAt, t.CompletedAt,
                t.Decisions.OrderBy(d => d.DecidedAt).Select(d => new DecisionDto(d.Id, d.ActorId, Name(d.ActorId), d.ActingForId, Name(d.ActingForId), d.Decision, d.Comment, d.DecidedAt)).ToList(),
                t.EscalatedAt, t.EscalatedTo)).ToList())).ToList();
    }

    private static ApprovalStarted Started(WorkflowRun run) => new(run.Id, run.DefinitionId, run.DefinitionVersion, run.Status);
}
