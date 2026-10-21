namespace Vpms.BuildingBlocks.Tenancy;

/// <summary>Row belongs to exactly one tenant. Every implementer gets a global query filter and an RLS policy.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>Row belongs to a tenant and to one of its legal entities.</summary>
public interface ILegalEntityScoped : ITenantOwned
{
    Guid LegalEntityId { get; set; }
}
