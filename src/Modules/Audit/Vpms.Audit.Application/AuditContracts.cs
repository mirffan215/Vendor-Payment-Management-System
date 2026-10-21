using Vpms.Audit.Domain;

namespace Vpms.Audit.Application;

public sealed record AuditQuery(
    Guid? LegalEntityId = null,
    Guid? ActorId = null,
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    string? CorrelationId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    long? AfterSequence = null,
    int Limit = 50)
{
    public const int MaxLimit = 200;
}

public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, long? NextAfterSequence);

public interface IAuditQueryService
{
    /// <param name="entityScope">null means tenant-wide access; otherwise only events of these legal entities.</param>
    Task<AuditPage> QueryAsync(AuditQuery query, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);
}

public sealed record ChainVerificationResult(bool IsValid, long EventsChecked, long? FirstBrokenSequence, string? Problem)
{
    public static ChainVerificationResult Ok(long checkedCount) => new(true, checkedCount, null, null);

    public static ChainVerificationResult Broken(long checkedCount, long? sequence, string problem) =>
        new(false, checkedCount, sequence, problem);
}

public interface IAuditChainVerifier
{
    Task<ChainVerificationResult> VerifyAsync(Guid tenantId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> ListTenantsWithEventsAsync(CancellationToken ct = default);
}

/// <summary>Publishes the chain head to storage outside the database (immutable blob, object lock, ...).</summary>
public interface IAuditAnchorSink
{
    /// <returns>An external reference to the published anchor.</returns>
    Task<string> PublishAsync(Guid tenantId, long sequence, string hash, CancellationToken ct = default);
}

public interface IAuditAnchorService
{
    Task<bool> AnchorAsync(Guid tenantId, CancellationToken ct = default);
}
