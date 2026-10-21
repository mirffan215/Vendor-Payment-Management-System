using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.CustomerAdmin.Domain;

/// <summary>How much a person may approve for one legal entity, within a validity window. Data only: the engine arrives in Phase 4.</summary>
public class ApprovalAuthority : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid UserId { get; set; }
    public decimal MaxAmount { get; set; }
    public string CurrencyCode { get; set; } = "";
    public string? DimensionScopeJson { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public string Status { get; set; } = "Active";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A routing rule of an entity's approval matrix. Data model only in Phase 2.</summary>
public class ApprovalMatrixRule : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public string ConditionsJson { get; set; } = "{}";
    public string ApproverResolutionJson { get; set; } = "{}";
    public int Levels { get; set; } = 1;
    public string Status { get; set; } = "Active";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
