using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Application;

public sealed record SyncRecordDto(
    Guid Id, Guid LegalEntityId, string Type, Guid ObjectId, string ObjectVersion, string Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastErrorCode, string? LastError,
    string? ErpReference, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, Guid? HandledBy, string? HandlingNote);

public sealed record SyncAttemptDto(int AttemptNo, DateTimeOffset At, string Outcome, string? Code, string? Message);

public sealed record SyncRecordDetailDto(SyncRecordDto Record, IReadOnlyList<SyncAttemptDto> Attempts);

public sealed record SyncSummaryDto(int Pending, int InFlight, int Succeeded, int DeadLetter, int Resolved);

public sealed record SyncRunResult(int Enqueued, int Processed, int Succeeded, int Retrying, int DeadLettered);

public interface IErpSyncService
{
    Task<SyncSummaryDto> SummaryAsync(IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<IReadOnlyList<SyncRecordDto>> ListAsync(string? status, string? type, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<SyncRecordDetailDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default);

    /// <summary>Puts a dead-lettered message back in the queue. A reason is required and the action is audited.</summary>
    Task<SyncRecordDto> RetryAsync(Guid id, string reason, CancellationToken ct = default);

    /// <summary>Records that a dead-lettered message was dealt with outside the queue.</summary>
    Task<SyncRecordDto> ResolveAsync(Guid id, string reason, string? erpReference, CancellationToken ct = default);
}

/// <summary>Queues what needs sending and sends what is due, for the current tenant.</summary>
public interface IErpOutbound
{
    /// <summary>Queues approved invoices and changed vendors that are not queued yet. Safe to run repeatedly.</summary>
    Task<int> EnqueueAsync(CancellationToken ct = default);

    /// <summary>Sends up to <paramref name="batch"/> due messages.</summary>
    Task<SyncRunResult> ProcessDueAsync(int batch, CancellationToken ct = default);
}

public interface IErpScan
{
    /// <summary>Tenants that have an approved ERP connection.</summary>
    Task<IReadOnlyList<Guid>> TenantsWithConnectionAsync(CancellationToken ct = default);
}

public sealed record InboundResult(int Received, int Duplicates, int Applied, int Failed, IReadOnlyList<string> PullErrors);

public sealed record ErpInvoiceStatusDto(string Reference, string State, DateTimeOffset? PaidAt, decimal Total, string CurrencyCode, DateTimeOffset SeenAt);

/// <summary>Pulls what the ERP knows (purchase orders, the status of posted invoices, vendor payments) into the inbox and applies it.</summary>
public interface IErpInbound
{
    /// <summary>Safe to repeat and to overlap: anything already received is recognised by its content and ignored.</summary>
    Task<InboundResult> PullAsync(CancellationToken ct = default);
}

public interface IErpInboundQuery
{
    /// <summary>What the ERP last reported about the invoice VPMS posted, or null when nothing has been reported.</summary>
    Task<ErpInvoiceStatusDto?> InvoiceStatusAsync(Guid invoiceId, CancellationToken ct = default);
}
