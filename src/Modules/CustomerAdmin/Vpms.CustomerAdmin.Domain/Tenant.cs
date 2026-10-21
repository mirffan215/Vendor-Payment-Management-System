namespace Vpms.CustomerAdmin.Domain;

/// <summary>A customer organisation (e.g. a holding group). Not tenant-owned: its Id is the tenant id.</summary>
public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Status { get; set; } = "Active";
    public string DefaultLocale { get; set; } = "en";
    public DateTimeOffset CreatedAt { get; set; }
}
