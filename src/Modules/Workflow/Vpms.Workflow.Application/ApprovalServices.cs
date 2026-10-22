using Vpms.Workflow.Contracts;

namespace Vpms.Workflow.Application;

public sealed record InboxItemDto(
    Guid TaskId, Guid RunId, string StepId, string SubjectType, Guid SubjectId, string SubjectLabel, Guid LegalEntityId,
    decimal Amount, string Currency, string Mode, int ApprovalsSoFar, int Approvers, DateTimeOffset CreatedAt, DateTimeOffset? DueAt);

public sealed record DecisionDto(
    Guid Id, Guid ActorId, string? ActorName, Guid? ActingForId, string? ActingForName, string Decision, string? Comment, DateTimeOffset DecidedAt);

public sealed record TaskDto(
    Guid Id, string StepId, string BranchPath, string Status, string Mode, int Quorum, IReadOnlyList<Guid> Assignees, IReadOnlyList<string?> AssigneeNames,
    DateTimeOffset CreatedAt, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, IReadOnlyList<DecisionDto> Decisions);

public sealed record RunDto(
    Guid Id, string SubjectType, Guid SubjectId, int SubjectVersion, string SubjectLabel, Guid LegalEntityId, string Status, string? StatusReason,
    int DefinitionVersion, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, decimal Amount, string Currency, bool NeedsAttention,
    IReadOnlyList<TaskDto> Tasks);

/// <param name="Decision">Approve, Reject or Return.</param>
/// <param name="ActingFor">Set when deciding as a delegate for another approver.</param>
public sealed record DecideRequest(string Decision, string? Comment, Guid? ActingFor);

public interface IApprovalService : IApprovalStarter
{
    /// <summary>Open tasks for the current user, optionally limited to legal entities in the user's approval scope.</summary>
    Task<IReadOnlyList<InboxItemDto>> InboxAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    /// <summary>The run a task belongs to, with all its tasks and decisions.</summary>
    Task<RunDto> GetRunOfTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>All runs for a subject, newest first (earlier runs stay as history after a restart).</summary>
    Task<IReadOnlyList<RunDto>> RunsForSubjectAsync(string subjectType, Guid subjectId, CancellationToken ct = default);

    Task<Guid> EntityOfTaskAsync(Guid taskId, CancellationToken ct = default);

    Task<RunDto> DecideAsync(Guid taskId, DecideRequest request, CancellationToken ct = default);

    /// <summary>Tells subject modules about runs that ended but whose outcome was not applied yet.</summary>
    Task<int> ApplyPendingOutcomesAsync(CancellationToken ct = default);
}

public interface IApprovalScan
{
    Task<IReadOnlyList<Guid>> TenantsWithPendingOutcomesAsync(CancellationToken ct = default);
}
