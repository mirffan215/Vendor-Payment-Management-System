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
    /// <summary>All matching events in sequence order, without paging limits (used by exports). The caller must bound how many it reads.</summary>
    IAsyncEnumerable<AuditEvent> StreamAsync(AuditQuery query, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

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
    /// <summary>Verifies the stored events and their links, the database anchors and the anchors published outside the database.</summary>
    Task<ChainVerificationResult> VerifyAsync(Guid tenantId, CancellationToken ct = default);

    /// <param name="checkExternalAnchors">False checks only the database itself (used when a restore is being assessed).</param>
    Task<ChainVerificationResult> VerifyAsync(Guid tenantId, bool checkExternalAnchors, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> ListTenantsWithEventsAsync(CancellationToken ct = default);
}

/// <summary>Publishes the chain head to storage outside the database (immutable blob, object lock, ...).</summary>
public interface IAuditAnchorSink
{
    /// <returns>An external reference to the published anchor.</returns>
    Task<string> PublishAsync(Guid tenantId, long sequence, string hash, CancellationToken ct = default);

    /// <summary>
    /// Records that the database was restored to an earlier state and that state was accepted by a person: anchors published earlier for later
    /// sequences no longer count. Written before new events reuse those sequence numbers.
    /// </summary>
    Task<string> PublishRestoreMarkerAsync(Guid tenantId, long lastSequence, string reason, CancellationToken ct = default);

    /// <summary>Everything published for the tenant, oldest first.</summary>
    Task<IReadOnlyList<ExternalAnchor>> ListAsync(Guid tenantId, CancellationToken ct = default);
}

public static class ExternalAnchorKind
{
    public const string Anchor = "anchor";
    public const string Restore = "restore";
}

/// <param name="Sequence">For an anchor, the event it vouches for. For a restore marker, the last sequence the restored database had.</param>
public sealed record ExternalAnchor(string Kind, long Sequence, string Hash, DateTimeOffset PublishedAt, string? Reason = null);

/// <summary>What a person decides after the database has been restored to an earlier state (see the restore runbook).</summary>
public interface IAuditRestoreService
{
    /// <summary>
    /// Accepts the restored chain: it must be internally valid. Publishes a restore marker outside the database and writes an audit event, so anchors
    /// published for the lost events stop counting and the chain can continue from where the backup ended.
    /// </summary>
    Task<ChainVerificationResult> AcceptRestoreAsync(Guid tenantId, string reason, CancellationToken ct = default);
}

public interface IAuditAnchorService
{
    Task<bool> AnchorAsync(Guid tenantId, CancellationToken ct = default);
}
