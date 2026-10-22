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

/// <summary>
/// Row belongs to one vendor of the tenant. While a vendor (portal) session is active, queries are additionally filtered to that
/// vendor and writes for any other vendor are refused. Staff scopes have no vendor and see all vendors of the tenant.
/// </summary>
public interface IVendorOwned : ITenantOwned
{
    Guid VendorId { get; set; }
}
