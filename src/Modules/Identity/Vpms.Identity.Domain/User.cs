using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Identity.Domain;

public static class UserStatus
{
    public const string Active = "Active";
    public const string Disabled = "Disabled";
}

public class User : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ProviderId { get; set; }
    public string ExternalSubject { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Locale { get; set; } = "en";
    public string Status { get; set; } = UserStatus.Active;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
