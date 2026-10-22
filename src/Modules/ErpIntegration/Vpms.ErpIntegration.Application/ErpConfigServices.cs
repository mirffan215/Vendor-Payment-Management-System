using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Application;

public sealed record ErpConfigDto(
    Guid? Id, string Kind, Guid? LegalEntityId, int VersionNo, string Status, ConnectionConfig? Connection, MappingConfig? Mapping, string Note, Guid? CreatedBy,
    DateTimeOffset? CreatedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionComment);

public sealed record ErpConfigStateDto(ErpConfigDto? Active, ErpConfigDto? Pending);

public sealed record ErpConfigChangeDto(ErpConfigDto Change, ErpConfigDto? Base);

public sealed record ConnectionTestDto(bool Ok, string? ErrorCode, string? Message);

public interface IErpConfigService
{
    Task<ErpConfigStateDto> GetConnectionAsync(CancellationToken ct = default);

    Task<ErpConfigDto> ProposeConnectionAsync(ConnectionConfig config, string note, CancellationToken ct = default);

    Task<ErpConfigStateDto> GetMappingAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<ErpConfigDto> ProposeMappingAsync(Guid legalEntityId, MappingConfig config, string note, CancellationToken ct = default);

    Task<IReadOnlyList<ErpConfigChangeDto>> ListChangesAsync(string? status, IReadOnlySet<Guid>? entityScope, bool includeTenantWide, CancellationToken ct = default);

    /// <summary>The legal entity of a change, or null for the tenant-wide connection.</summary>
    Task<Guid?> EntityOfChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<ErpConfigDto> ApproveAsync(Guid changeId, string? comment, CancellationToken ct = default);

    Task<ErpConfigDto> RejectAsync(Guid changeId, string comment, CancellationToken ct = default);

    Task<ErpConfigDto> CancelAsync(Guid changeId, CancellationToken ct = default);

    /// <summary>Asks the ERP to confirm the active connection and mapping work. Never returns or logs the secret.</summary>
    Task<ConnectionTestDto> TestAsync(Guid legalEntityId, CancellationToken ct = default);
}
