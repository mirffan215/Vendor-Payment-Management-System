using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;

namespace Vpms.Workflow.Domain;

public static class RunStatus
{
    public const string Active = "Active";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Returned = "Returned";
    public const string Cancelled = "Cancelled";
}

public static class TaskStatus
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public static class DecisionKind
{
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string Return = "Return";

    public static readonly IReadOnlyList<string> Values = [Approve, Reject, Return];
}

/// <summary>Turns an approver in a definition into the people it currently means (for a role: its holders for the legal entity).</summary>
public delegate IReadOnlyList<Guid> ApproverResolver(ApproverSpec approver);

/// <summary>A person's recorded decision on a task. Append-only.</summary>
public class TaskDecision
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid ActorId { get; set; }

    /// <summary>Set when the actor decided as a delegate: the approver they stood in for.</summary>
    public Guid? ActingForId { get; set; }

    public string Decision { get; set; } = DecisionKind.Approve;
    public string? Comment { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>One approval step waiting for (or having received) its decisions.</summary>
public class ApprovalTask
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string StepId { get; set; } = "";

    /// <summary>Which parallel branches this task sits in, outermost first, e.g. "p1:0/p2:1".</summary>
    public string BranchPath { get; set; } = "";

    public List<Guid> Assignees { get; set; } = [];
    public string Mode { get; set; } = ApprovalMode.AnyOne;
    public int Quorum { get; set; }
    public string Status { get; set; } = TaskStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<TaskDecision> Decisions { get; set; } = [];

    public int Approvals => Decisions.Count(d => d.Decision == DecisionKind.Approve);

    public bool IsSatisfied => Mode switch
    {
        ApprovalMode.All => Assignees.Count > 0 && Approvals >= Assignees.Count,
        ApprovalMode.Quorum => Approvals >= Math.Min(Quorum, Math.Max(Assignees.Count, 1)) && Assignees.Count > 0,
        _ => Approvals >= 1,
    };
}

/// <summary>How many branches of a parallel step have finished.</summary>
public class JoinArrival
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Key { get; set; } = "";
    public int Arrived { get; set; }
    public bool Fired { get; set; }
}

/// <summary>What a person is deciding, and on whose behalf.</summary>
public sealed record DecisionRequest(
    Guid Actor,
    string Decision,
    string? Comment,
    DateTimeOffset Now,
    Guid? ActingFor = null,
    Func<Guid, bool>? HasAuthority = null);

/// <summary>
/// One run of a workflow definition for one subject. Pure state machine: it owns who must decide what, enforces
/// maker-checker and the different-person rule on every decision, and reports a final status. It is pinned to a
/// definition version by the caller, which passes that version's spec to every call.
/// </summary>
public class WorkflowRun
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SubjectType { get; set; } = "";
    public Guid SubjectId { get; set; }
    public int SubjectVersion { get; set; }
    public Guid DefinitionId { get; set; }
    public int DefinitionVersion { get; set; }
    public string Status { get; set; } = RunStatus.Active;
    public string? StatusReason { get; set; }
    public Guid StartedBy { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The people who prepared the subject; none of them may decide it.</summary>
    public List<Guid> Makers { get; set; } = [];

    public SubjectFacts Facts { get; set; } = SubjectFacts.Of(0);
    public List<ApprovalTask> Tasks { get; set; } = [];
    public List<JoinArrival> Joins { get; set; } = [];

    [System.ComponentModel.DataAnnotations.Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public IEnumerable<ApprovalTask> PendingTasks => Tasks.Where(t => t.Status == TaskStatus.Pending);

    /// <summary>A pending task nobody can decide (everyone eligible is a maker or already decided). Needs an administrator or escalation.</summary>
    public bool NeedsAttention => Status == RunStatus.Active && PendingTasks.Any(t => t.Assignees.Count == 0);

    public static WorkflowRun Start(WorkflowDefinitionSpec spec, SubjectFacts facts, IEnumerable<Guid> makers, Guid startedBy,
        ApproverResolver resolver, DateTimeOffset now)
    {
        var errors = DefinitionValidator.Validate(spec);
        if (errors.Count > 0) throw new Errors.WorkflowDefinitionException("invalid_definition", errors[0].Message);

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            Facts = facts,
            Makers = makers.Where(m => m != Guid.Empty).Distinct().ToList(),
            StartedBy = startedBy,
            StartedAt = now,
        };
        run.Walk(spec, spec.Start, "", resolver, now, firstStep: true);
        return run;
    }

    public void Decide(WorkflowDefinitionSpec spec, Guid taskId, DecisionRequest request, ApproverResolver resolver)
    {
        if (Status != RunStatus.Active) throw new ConflictException("run_not_active", "This approval has already finished.");
        var task = Tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new NotFoundException("Approval task");
        if (task.Status != TaskStatus.Pending) throw new ConflictException("task_not_pending", "This approval step has already been decided.");
        if (!DecisionKind.Values.Contains(request.Decision)) throw new ValidationFailedException("decision_invalid", "Choose approve, reject or return.");
        if (request.Decision != DecisionKind.Approve && (string.IsNullOrWhiteSpace(request.Comment) || request.Comment.Length > 1000))
        {
            throw new ValidationFailedException("comment_required", "A comment is required to reject or return (max 1000 characters).");
        }

        // The person whose authority is used: the approver themself, or the one they stand in for.
        var effective = request.ActingFor ?? request.Actor;
        EnsureIndependent(request.Actor, effective);
        if (!task.Assignees.Contains(effective))
        {
            throw new ValidationFailedException("not_an_approver", "You are not an approver for this step.", 403);
        }

        if (request.Decision == DecisionKind.Approve && request.HasAuthority is not null && !request.HasAuthority(effective))
        {
            throw new ValidationFailedException("approval_limit_exceeded", "The amount is above the approval authority of this approver.", 403);
        }

        task.Decisions.Add(new TaskDecision
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ActorId = request.Actor,
            ActingForId = request.ActingFor is not null && request.ActingFor != request.Actor ? request.ActingFor : null,
            Decision = request.Decision, Comment = request.Comment?.Trim(), DecidedAt = request.Now,
        });

        switch (request.Decision)
        {
            case DecisionKind.Reject: Finish(RunStatus.Rejected, request.Comment, request.Now); return;
            case DecisionKind.Return: Finish(RunStatus.Returned, request.Comment, request.Now); return;
        }

        if (!task.IsSatisfied) return;

        task.Status = TaskStatus.Completed;
        task.CompletedAt = request.Now;
        Walk(spec, spec.Find(task.StepId)!.Next, task.BranchPath, resolver, request.Now);
    }

    /// <summary>Ends the run without a decision, for example when the subject changed materially and approval restarts.</summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status != RunStatus.Active) throw new ConflictException("run_not_active", "This approval has already finished.");
        Finish(RunStatus.Cancelled, reason, now);
    }

    /// <summary>Maker-checker and the different-person rule, for the actor and for whoever they stand in for.</summary>
    private void EnsureIndependent(Guid actor, Guid effective)
    {
        foreach (var maker in Makers)
        {
            MakerCheckerGuard.EnsureDifferent(maker, actor, "approve invoice");
            MakerCheckerGuard.EnsureDifferent(maker, effective, "approve invoice");
        }

        var prior = Tasks.SelectMany(t => t.Decisions)
            .SelectMany(d => new[] { new PriorActor("earlier approver", d.ActorId), new PriorActor("earlier approver", d.ActingForId ?? d.ActorId) })
            .ToList();
        DifferentPersonRule.Ensure(actor, "approve a further step", prior);
        DifferentPersonRule.Ensure(effective, "approve a further step", prior);
    }

    private void Finish(string status, string? reason, DateTimeOffset now)
    {
        Status = status;
        StatusReason = reason?.Trim();
        CompletedAt = now;
        foreach (var t in PendingTasks.ToList())
        {
            t.Status = TaskStatus.Cancelled;
            t.CompletedAt = now;
        }
    }

    private static string Segment(string path, string tail) => path.Length == 0 ? tail : $"{path}/{tail}";

    /// <summary>Follows the definition from a step until it must wait for a person or the path ends.</summary>
    private void Walk(WorkflowDefinitionSpec spec, string? stepId, string path, ApproverResolver resolver, DateTimeOffset now, bool firstStep = false)
    {
        while (true)
        {
            if (stepId is null)
            {
                ArriveAtEnd(spec, path, resolver, now);
                return;
            }

            var step = spec.Find(stepId)!;
            switch (step.Type)
            {
                case StepType.Condition:
                    stepId = step.Cases.FirstOrDefault(c => Conditions.Matches(c.All, Facts))?.Next ?? step.Default;
                    break;

                case StepType.Parallel:
                    for (var i = 0; i < step.Branches.Count; i++)
                    {
                        Walk(spec, step.Branches[i], Segment(path, $"{step.Id}:{i}"), resolver, now, firstStep);
                    }

                    return;

                default:
                    AddTask(step, path, resolver, now, firstStep);
                    return;
            }
        }
    }

    private void ArriveAtEnd(WorkflowDefinitionSpec spec, string path, ApproverResolver resolver, DateTimeOffset now)
    {
        if (path.Length == 0)
        {
            Finish(RunStatus.Approved, null, now);
            return;
        }

        var cut = path.LastIndexOf('/');
        var parent = cut < 0 ? "" : path[..cut];
        var last = cut < 0 ? path : path[(cut + 1)..];
        var parallelId = last[..last.IndexOf(':')];
        var parallel = spec.Find(parallelId)!;
        var key = Segment(parent, parallelId);

        var join = Joins.FirstOrDefault(j => j.Key == key);
        if (join is null)
        {
            join = new JoinArrival { Id = Guid.NewGuid(), RunId = Id, Key = key };
            Joins.Add(join);
        }

        join.Arrived++;
        if (join.Fired) return;
        if (parallel.Join == JoinMode.All && join.Arrived < parallel.Branches.Count) return;

        join.Fired = true;
        if (parallel.Join == JoinMode.Any)
        {
            foreach (var t in PendingTasks.Where(t => t.BranchPath.StartsWith(key + ":", StringComparison.Ordinal)).ToList())
            {
                t.Status = TaskStatus.Cancelled;
                t.CompletedAt = now;
            }
        }

        Walk(spec, parallel.Next, parent, resolver, now);
    }

    private void AddTask(StepSpec step, string path, ApproverResolver resolver, DateTimeOffset now, bool firstStep)
    {
        var excluded = Makers.Concat(Tasks.SelectMany(t => t.Decisions).SelectMany(d => new[] { d.ActorId, d.ActingForId ?? d.ActorId })).ToHashSet();
        var everyone = step.Approvers.SelectMany(a => resolver(a)).Distinct().ToList();
        var eligible = everyone.Where(u => !excluded.Contains(u)).ToList();

        if (firstStep && eligible.Count == 0)
        {
            throw new ValidationFailedException("no_eligible_approver",
                "No approver is available for the first step: the approvers are missing or are the people who prepared the invoice.");
        }

        Tasks.Add(new ApprovalTask
        {
            Id = Guid.NewGuid(), RunId = Id, StepId = step.Id, BranchPath = path, Assignees = eligible,
            Mode = step.Mode, Quorum = step.Quorum, CreatedAt = now,
            DueAt = step.DueHours is { } d ? now.AddHours(d) : null,
            ReminderAt = step.ReminderHours is { } r ? now.AddHours(r) : null,
        });
    }
}
