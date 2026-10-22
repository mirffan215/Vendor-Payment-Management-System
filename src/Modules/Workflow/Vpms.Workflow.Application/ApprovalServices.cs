using Vpms.Workflow.Contracts;

namespace Vpms.Workflow.Application;

public sealed record InboxItemDto(
    Guid TaskId, Guid RunId, string StepId, string SubjectType, Guid SubjectId, string SubjectLabel, Guid LegalEntityId,
    decimal Amount, string Currency, string Mode, int ApprovalsSoFar, int Approvers, DateTimeOffset CreatedAt, DateTimeOffset? DueAt,
    bool Overdue = false, bool Escalated = false, Guid? ActingForId = null, string? ActingForName = null);

public sealed record DecisionDto(
    Guid Id, Guid ActorId, string? ActorName, Guid? ActingForId, string? ActingForName, string Decision, string? Comment, DateTimeOffset DecidedAt);

public sealed record TaskDto(
    Guid Id, string StepId, string BranchPath, string Status, string Mode, int Quorum, IReadOnlyList<Guid> Assignees, IReadOnlyList<string?> AssigneeNames,
    DateTimeOffset CreatedAt, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, IReadOnlyList<DecisionDto> Decisions,
    DateTimeOffset? EscalatedAt = null, IReadOnlyList<Guid>? EscalatedTo = null);

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

public sealed record DelegationDto(
    Guid Id, Guid LegalEntityId, Guid DelegatorId, string? DelegatorName, Guid DelegateId, string? DelegateName, DateTimeOffset ValidFrom, DateTimeOffset ValidTo,
    decimal? MaxAmount, string Status, string Reason, DateTimeOffset CreatedAt, Guid CreatedBy);

/// <param name="DelegatorId">Leave empty to delegate your own approvals. Setting someone else's needs the workflow-administration permission.</param>
public sealed record CreateDelegation(Guid LegalEntityId, Guid? DelegatorId, Guid DelegateId, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, decimal? MaxAmount, string Reason);

public sealed record ApproverOption(Guid Id, string Name);

public interface IDelegationService
{
    /// <summary>People who can approve invoices of the legal entity: the choices for a delegate or a workflow approver.</summary>
    Task<IReadOnlyList<ApproverOption>> ApproversAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>Delegations the current user gave or received (newest first), optionally only those still in force.</summary>
    Task<IReadOnlyList<DelegationDto>> MineAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>All delegations of a legal entity, for administrators.</summary>
    Task<IReadOnlyList<DelegationDto>> ForEntityAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<DelegationDto> CreateAsync(CreateDelegation request, bool onBehalfOfOthersAllowed, CancellationToken ct = default);

    Task<DelegationDto> RevokeAsync(Guid id, bool administrator, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default);
}

public sealed record NoticeDto(Guid Id, string Kind, Guid RunId, Guid TaskId, string SubjectLabel, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record TimerRunResult(int Reminders, int Escalations);

public interface IWorkflowTimers
{
    /// <summary>Sends due reminders and escalates overdue tasks of the current tenant. Safe to run repeatedly: each happens once per task.</summary>
    Task<TimerRunResult> RunDueAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> TenantsWithDueWorkAsync(CancellationToken ct = default);

    Task<IReadOnlyList<NoticeDto>> NoticesAsync(bool unreadOnly, CancellationToken ct = default);

    Task MarkNoticeReadAsync(Guid id, CancellationToken ct = default);
}
