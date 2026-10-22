using Vpms.Invoices.Domain.Verification;

namespace Vpms.Invoices.Application;

public sealed record RuleSetDto(
    Guid? Id, Guid LegalEntityId, int VersionNo, string Status, RuleConfig Config, string Note, IReadOnlyList<string> LoosenedRules, Guid? CreatedBy,
    DateTimeOffset? CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

/// <param name="Active">The settings in force. "Version 0" with status Default means the entity still uses the built-in defaults.</param>
/// <param name="Pending">A change waiting for a second person, if there is one.</param>
public sealed record EntityRulesDto(RuleSetDto Active, RuleSetDto? Pending);

public sealed record ProposeRuleChange(IReadOnlyDictionary<string, RuleSetting> Rules, string Note);

public sealed record RuleChangeDto(RuleSetDto Change, RuleSetDto Base);

public interface IVerificationRuleSetService
{
    Task<EntityRulesDto> GetAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>What the verification engine runs with: the version number (0 = defaults) and its configuration.</summary>
    Task<(int VersionNo, RuleConfig Config)> ActiveConfigAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<RuleSetDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>A tightening change is active at once; a change that loosens any rule waits for a different approver.</summary>
    Task<RuleSetDto> ProposeAsync(Guid legalEntityId, ProposeRuleChange request, CancellationToken ct = default);

    Task<IReadOnlyList<RuleChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<RuleSetDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default);

    Task<RuleSetDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<RuleSetDto> CancelAsync(Guid changeId, CancellationToken ct = default);
}
