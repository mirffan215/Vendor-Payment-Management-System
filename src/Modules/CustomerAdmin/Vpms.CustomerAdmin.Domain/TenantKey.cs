using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.CustomerAdmin.Domain;

/// <summary>A tenant's data or index key, stored wrapped by the key-encryption key. Never holds plaintext key material.</summary>
public class TenantKey : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Purpose { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "Active";
    public string KekId { get; set; } = "";
    public byte[] WrappedKey { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
