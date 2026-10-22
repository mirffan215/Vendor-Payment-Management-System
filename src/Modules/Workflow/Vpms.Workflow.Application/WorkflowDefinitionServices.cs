using Vpms.Workflow.Domain;

namespace Vpms.Workflow.Application;

public sealed record DefinitionDto(
    Guid? Id, Guid LegalEntityId, string SubjectType, int VersionNo, string Status, string Source, WorkflowDefinitionSpec? Spec, string Note,
    Guid? CreatedBy, DateTimeOffset? CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

/// <param name="Active">The version in force; null while the entity has none (invoices cannot start approval).</param>
/// <param name="Pending">A version waiting for a second person, if there is one.</param>
public sealed record EntityWorkflowDto(DefinitionDto? Active, DefinitionDto? Pending);

public sealed record DefinitionChangeDto(DefinitionDto Change, DefinitionDto? Base);

/// <param name="Spec">The workflow graph; leave empty with <paramref name="FromMatrix"/> to compile the entity's approval matrix.</param>
public sealed record ProposeDefinition(WorkflowDefinitionSpec? Spec, bool FromMatrix, string Note);

public sealed record DefinitionIssue(string Code, string Message, string? StepId);

public sealed record SimulationRequest(
    WorkflowDefinitionSpec? Spec, decimal Amount, string Currency, Guid? VendorId, string? Source,
    IReadOnlyDictionary<string, string>? Dimensions, Guid? Maker);

public sealed record SimulatedStep(string StepId, IReadOnlyList<Guid> Approvers, string Mode);

/// <param name="Route">Steps in the order they would be asked, assuming everyone approves.</param>
/// <param name="Problem">Why the invoice would have no route or no eligible approver, if so.</param>
public sealed record SimulationDto(string Outcome, IReadOnlyList<SimulatedStep> Route, IReadOnlyList<DefinitionIssue> Issues, string? Problem);

/// <summary>The version a new run is pinned to.</summary>
public sealed record ActiveDefinition(Guid Id, int VersionNo, string Hash, WorkflowDefinitionSpec Spec);

public interface IWorkflowDefinitionService
{
    Task<EntityWorkflowDto> GetAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<DefinitionDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>Stores the next version as Pending. It takes effect when a different person approves it.</summary>
    Task<DefinitionDto> ProposeAsync(Guid legalEntityId, ProposeDefinition request, CancellationToken ct = default);

    IReadOnlyList<DefinitionIssue> Validate(WorkflowDefinitionSpec spec);

    Task<IReadOnlyList<DefinitionChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<DefinitionDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default);

    Task<DefinitionDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<DefinitionDto> CancelAsync(Guid changeId, CancellationToken ct = default);

    /// <summary>Compiles the entity's current approval matrix without storing it, so it can be reviewed first.</summary>
    Task<WorkflowDefinitionSpec> PreviewMatrixAsync(Guid legalEntityId, CancellationToken ct = default);

    /// <summary>What would happen to an invoice with these facts, using the active version or a draft spec. Stores nothing.</summary>
    Task<SimulationDto> SimulateAsync(Guid legalEntityId, SimulationRequest request, CancellationToken ct = default);

    Task<ActiveDefinition?> GetActiveAsync(Guid legalEntityId, string subjectType, CancellationToken ct = default);

    Task<ActiveDefinition?> GetVersionAsync(Guid definitionId, CancellationToken ct = default);
}

public sealed record MaterialityDto(
    Guid? Id, Guid LegalEntityId, int VersionNo, string Status, MaterialityConfig Config, string Note, Guid? CreatedBy, DateTimeOffset? CreatedAt,
    Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

/// <param name="Active">The settings in force. Version 0 with status Default means the entity still uses the built-in defaults (every change is material).</param>
public sealed record EntityMaterialityDto(MaterialityDto Active, MaterialityDto? Pending);

public sealed record MaterialityChangeDto(MaterialityDto Change, MaterialityDto Base);

public interface IMaterialityPolicyService
{
    Task<EntityMaterialityDto> GetAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<MaterialityDto>> HistoryAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<MaterialityDto> ProposeAsync(Guid legalEntityId, MaterialityConfig config, string note, CancellationToken ct = default);

    Task<IReadOnlyList<MaterialityChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<Guid> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<MaterialityDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default);

    Task<MaterialityDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<MaterialityDto> CancelAsync(Guid changeId, CancellationToken ct = default);
}
