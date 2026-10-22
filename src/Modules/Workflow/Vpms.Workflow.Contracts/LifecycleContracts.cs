namespace Vpms.Workflow.Contracts;

public sealed record MaterialityResult(bool Material, IReadOnlyList<string> ChangedFields);

/// <summary>
/// Decides whether an edit to something already sent for approval is big enough to need approval again, using the legal entity's
/// materiality settings. Subjects give snapshots of the fields they care about.
/// </summary>
public interface IMaterialityEvaluator
{
    Task<MaterialityResult> EvaluateAsync(Guid legalEntityId, string subjectType, IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> current, CancellationToken ct = default);
}

/// <summary>What a subject module can do to an approval that is still running when the subject changes.</summary>
public interface IApprovalLifecycle
{
    /// <summary>Ends the active run of a subject, if any, without a decision (the subject changed materially).</summary>
    Task CancelActiveAsync(string subjectType, Guid subjectId, string reason, CancellationToken ct = default);

    /// <summary>
    /// A small edit does not restart approval. The run follows the new version and amount, and the editor can no longer decide it.
    /// </summary>
    Task RebindAsync(string subjectType, Guid subjectId, int newVersion, decimal newAmount, Guid editor, CancellationToken ct = default);
}
