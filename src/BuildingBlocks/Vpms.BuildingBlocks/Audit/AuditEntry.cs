namespace Vpms.BuildingBlocks.Audit;

public static class AuditActorType
{
    public const string User = "User";
    public const string Delegate = "Delegate";
    public const string System = "System";
    public const string Anonymous = "Anonymous";
}

/// <summary>What happened. Actor, tenant, correlation id and IP are filled from the request context when omitted.</summary>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    string? EntityId = null,
    Guid? LegalEntityId = null,
    string? ChangeSummaryJson = null,
    string? BeforeHash = null,
    string? AfterHash = null,
    Guid? TenantId = null,
    Guid? ActorId = null,
    string? ActorType = null,
    Guid? OnBehalfOfId = null);

/// <summary>An entry with its context resolved, ready to be chained and stored.</summary>
public sealed record ResolvedAuditEntry(
    Guid TenantId,
    Guid? LegalEntityId,
    string ActorType,
    Guid? ActorId,
    Guid? OnBehalfOfId,
    string Action,
    string EntityType,
    string? EntityId,
    string? CorrelationId,
    string? IpAddress,
    string? BeforeHash,
    string? AfterHash,
    string? ChangeSummaryJson);

/// <summary>Per-request audit context, set by the host (actor, correlation id, client address).</summary>
public sealed class AuditContext
{
    public Guid? ActorId { get; set; }
    public string? ActorType { get; set; }
    public Guid? OnBehalfOfId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
}

public interface IAuditPort
{
    /// <summary>
    /// Buffers the entry. It is written in the SAME database transaction as the next SaveChanges of any tenant
    /// context in this scope, so a committed business change always has its audit record and vice versa.
    /// </summary>
    void Record(AuditEntry entry);

    /// <summary>For events with no business change (e.g. denied sign-in): writes immediately in its own transaction.</summary>
    Task WriteNowAsync(AuditEntry entry, CancellationToken ct = default);
}
