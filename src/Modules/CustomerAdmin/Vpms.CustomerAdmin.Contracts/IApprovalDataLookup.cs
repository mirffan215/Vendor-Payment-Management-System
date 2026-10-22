namespace Vpms.CustomerAdmin.Contracts;

/// <summary>An active routing rule of a legal entity's approval matrix.</summary>
public sealed record MatrixRuleInfo(Guid Id, string Name, int Priority, string ConditionsJson, string ApproverResolutionJson, int Levels);

/// <summary>Lets the workflow module read approval matrices and approval authorities without touching customer-admin tables.</summary>
public interface IApprovalDataLookup
{
    /// <summary>Active matrix rules of the entity, lowest priority number first.</summary>
    Task<IReadOnlyList<MatrixRuleInfo>> GetMatrixRulesAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>
    /// Whether the user holds an active approval authority for the entity that covers the amount in that currency on that date,
    /// and whose dimension scope (when it has one) matches the invoice. No authority means no right to approve.
    /// </summary>
    Task<bool> HasAuthorityAsync(Guid legalEntityId, Guid userId, decimal amount, string currency,
        IReadOnlyDictionary<string, string> dimensions, DateTimeOffset at, CancellationToken ct = default);
}
