using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.CustomerAdmin.Domain;

public class LegalEntity : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string? LegalNameAr { get; set; }

    /// <summary>Encrypted tax registration number (VAT). Never stored or logged in clear text.</summary>
    public string? TaxRegistrationNoCipher { get; set; }
    public string? TaxRegistrationNoIndex { get; set; }
    public string CountryCode { get; set; } = "";
    public string BaseCurrency { get; set; } = "";
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; }
}
