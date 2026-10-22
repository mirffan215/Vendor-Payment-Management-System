namespace Vpms.Workflow.Contracts;

/// <summary>Who took part in an approved item, for modules that must keep those people away from the next step (payment release).</summary>
public interface IApprovalHistory
{
    /// <summary>Everyone who approved the subject in its latest Approved run: the deciders and anyone they acted for.</summary>
    Task<IReadOnlyList<Guid>> GetApproversAsync(string subjectType, Guid subjectId, CancellationToken ct = default);
}
