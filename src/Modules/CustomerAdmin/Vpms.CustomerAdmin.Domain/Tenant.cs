namespace Vpms.CustomerAdmin.Domain;

public static class TenantStatus
{
    public const string Active = "Active";

    /// <summary>Sign-in and background work have stopped; data is intact and the tenant can be resumed.</summary>
    public const string Suspended = "Suspended";

    /// <summary>Left the service: keys destroyed and files deleted (ADR-0024). Final.</summary>
    public const string Offboarded = "Offboarded";
}

/// <summary>A customer organisation (e.g. a holding group). Not tenant-owned: its Id is the tenant id.</summary>
public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Status { get; set; } = TenantStatus.Active;

    /// <summary>When the status last changed (suspension starts the cooling-off period before keys can be destroyed).</summary>
    public DateTimeOffset? StatusChangedAt { get; set; }
    public string DefaultLocale { get; set; } = "en";
    public string DefaultCalendar { get; set; } = "gregorian";
    public string? PrimaryColor { get; set; }
    public Guid? LogoAttachmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
