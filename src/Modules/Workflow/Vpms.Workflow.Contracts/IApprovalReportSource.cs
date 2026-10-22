namespace Vpms.Workflow.Contracts;

public sealed record ApprovalTaskRow(
    Guid TaskId, Guid RunId, Guid LegalEntityId, string SubjectType, string SubjectLabel, string StepId, IReadOnlyList<Guid> Assignees, string TaskStatus, string RunStatus,
    DateTimeOffset CreatedAt, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, DateTimeOffset? EscalatedAt, Guid? DecidedBy);

/// <summary>Read-only queries for the approval-delay report.</summary>
public interface IApprovalReportSource
{
    /// <summary>Approval tasks created in the period (inclusive dates, UTC), newest first.</summary>
    Task<IReadOnlyList<ApprovalTaskRow>> TasksAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, DateOnly from, DateOnly to, int max, CancellationToken ct = default);
}
