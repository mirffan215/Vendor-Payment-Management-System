using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Application;

public sealed record ReconRunDto(
    Guid Id, Guid LegalEntityId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string Status, string? ErrorCode, int VpmsInvoices, int ErpDocuments, int NewItems, int OpenItems, int ClearedItems);

public sealed record ReconItemDto(
    Guid Id, Guid LegalEntityId, string Type, Guid? InvoiceId, string? InvoiceNumber, string? ErpReference, string VpmsValue, string ErpValue, string Status,
    DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, Guid? ProposedBy, string? ProposedReason, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionReason);

public sealed record ReconSummaryDto(int Open, int PendingAcceptance, int Accepted, int Resolved, IReadOnlyDictionary<string, int> OpenByType);

public interface IReconciliationService
{
    /// <summary>Compares every invoice VPMS posted to the entity's ERP company with what the ERP holds. Safe to repeat.</summary>
    Task<ReconRunDto> RunAsync(Guid legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<ReconRunDto>> RunsAsync(Guid? legalEntityId, IReadOnlySet<Guid>? entityScope, int take, CancellationToken ct = default);

    Task<ReconSummaryDto> SummaryAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<IReadOnlyList<ReconItemDto>> ItemsAsync(Guid? legalEntityId, string? status, string? type, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    /// <summary>The report as CSV, one row per difference. Cells that could be read as formulas by a spreadsheet are neutralised.</summary>
    Task<string> ExportCsvAsync(Guid? legalEntityId, string? status, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<Guid> EntityOfItemAsync(Guid itemId, CancellationToken ct = default);

    Task<ReconItemDto> ResolveAsync(Guid itemId, string reason, CancellationToken ct = default);

    Task<ReconItemDto> ProposeAcceptanceAsync(Guid itemId, string reason, CancellationToken ct = default);

    Task<ReconItemDto> ApproveAcceptanceAsync(Guid itemId, string? comment, CancellationToken ct = default);

    Task<ReconItemDto> RejectAcceptanceAsync(Guid itemId, string reason, CancellationToken ct = default);
}
