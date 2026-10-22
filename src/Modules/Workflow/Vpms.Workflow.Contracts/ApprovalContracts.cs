namespace Vpms.Workflow.Contracts;

/// <summary>What the workflow engine needs to know about the thing being approved. Subjects (invoices, later payment requests) build this.</summary>
/// <param name="Makers">Everyone who prepared the subject; none of them may decide it. The person starting the approval is added by the engine.</param>
public sealed record ApprovalSubject(
    string SubjectType, Guid SubjectId, int SubjectVersion, Guid LegalEntityId, string Label,
    decimal Amount, string Currency, Guid? VendorId, string Source,
    IReadOnlyDictionary<string, string> Dimensions, IReadOnlyCollection<Guid> Makers);

/// <param name="Status">Active, or Approved when the definition needed no decision for this subject.</param>
public sealed record ApprovalStarted(Guid RunId, Guid DefinitionId, int DefinitionVersion, string Status);

/// <summary>Starts an approval for a subject on the legal entity's active workflow. Repeating the call for the same version returns the same run.</summary>
public interface IApprovalStarter
{
    Task<ApprovalStarted> StartAsync(ApprovalSubject subject, CancellationToken ct = default);
}

public static class ApprovalOutcomes
{
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Returned = "Returned";
    public const string Cancelled = "Cancelled";
}

public sealed record ApprovalOutcome(string SubjectType, Guid SubjectId, int SubjectVersion, Guid RunId, string Outcome, string? Reason, DateTimeOffset At);

/// <summary>
/// Implemented by the module that owns a subject type. The engine calls it when a run ends; it must be idempotent because a
/// background sweep calls it again if the first attempt did not complete.
/// </summary>
public interface IApprovalOutcomeHandler
{
    string SubjectType { get; }

    Task ApplyAsync(ApprovalOutcome outcome, CancellationToken ct = default);
}
