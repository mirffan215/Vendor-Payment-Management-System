namespace Vpms.Workflow.Domain;

/// <summary>One scripted action: this person decides the first pending task they can decide.</summary>
public sealed record SimAction(Guid User, string Decision = DecisionKind.Approve, string? Comment = null, Guid? ActingFor = null);

public sealed record SimResult(WorkflowRun Run, IReadOnlyList<string> Trace);

/// <summary>
/// Runs a definition against synthetic facts and scripted decisions without storing anything. Used by the pattern
/// tests, and by the "what would happen to this invoice" preview in the administration screen.
/// </summary>
public static class WorkflowSimulator
{
    public static SimResult Run(WorkflowDefinitionSpec spec, SubjectFacts facts, IEnumerable<Guid> makers, ApproverResolver resolver,
        IEnumerable<SimAction> script, DateTimeOffset? now = null)
    {
        var clock = now ?? new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var trace = new List<string>();
        var makerList = makers.ToList();
        var run = WorkflowRun.Start(spec, facts, makerList, makerList.FirstOrDefault(), resolver, clock);
        Note(run, trace);

        foreach (var action in script)
        {
            var who = action.ActingFor ?? action.User;
            var task = run.PendingTasks.FirstOrDefault(t => t.Assignees.Contains(who))
                ?? throw new InvalidOperationException($"{action.User} has no pending task in the simulation.");
            run.Decide(spec, task.Id, new DecisionRequest(action.User, action.Decision, action.Comment ?? (action.Decision == DecisionKind.Approve ? null : "simulation"),
                clock, action.ActingFor), resolver);
            trace.Add($"{action.Decision}:{task.StepId}:{action.User}");
            Note(run, trace);
        }

        return new SimResult(run, trace);
    }

    /// <summary>The route an invoice takes if everyone approves: each pending task is approved by its first assignee.</summary>
    public static SimResult Preview(WorkflowDefinitionSpec spec, SubjectFacts facts, IEnumerable<Guid> makers, ApproverResolver resolver)
    {
        var clock = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        var trace = new List<string>();
        var makerList = makers.ToList();
        var run = WorkflowRun.Start(spec, facts, makerList, makerList.FirstOrDefault(), resolver, clock);
        Note(run, trace);

        for (var guard = 0; run.Status == RunStatus.Active && guard < DefinitionValidator.MaxSteps * 4; guard++)
        {
            var task = run.PendingTasks.FirstOrDefault(t => t.Assignees.Count > 0);
            if (task is null) break;
            var user = task.Assignees.First(u => task.Decisions.All(d => d.ActorId != u));
            run.Decide(spec, task.Id, new DecisionRequest(user, DecisionKind.Approve, null, clock), resolver);
            Note(run, trace);
        }

        return new SimResult(run, trace);
    }

    private static void Note(WorkflowRun run, List<string> trace)
    {
        foreach (var t in run.Tasks.Where(t => t.Decisions.Count == 0 && !trace.Contains($"task:{t.StepId}:{t.Id}")))
        {
            trace.Add($"task:{t.StepId}:{t.Id}");
        }
    }
}
