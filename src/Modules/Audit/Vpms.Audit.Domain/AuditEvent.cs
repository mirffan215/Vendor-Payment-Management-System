using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Domain;

/// <summary>One immutable, hash-chained audit record. Rows are only ever inserted.</summary>
public class AuditEvent : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string ActorType { get; set; } = "";
    public Guid? ActorId { get; set; }
    public Guid? OnBehalfOfId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string? ChangeSummaryJson { get; set; }
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>Latest chain position per tenant. Bookkeeping only; the events themselves are the evidence.</summary>
public class AuditChainHead : ITenantOwned
{
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Records that the chain head was copied to storage outside the database.</summary>
public class AuditAnchor : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset AnchoredAt { get; set; }
    public string ExternalRef { get; set; } = "";
}
