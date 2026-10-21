using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Vendors.Domain;

public static class VendorDocumentType
{
    public const string CommercialRegistration = "CommercialRegistration";
    public const string TaxCertificate = "TaxCertificate";
    public const string BankLetter = "BankLetter";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = [CommercialRegistration, TaxCertificate, BankLetter, Other];

    /// <summary>Documents that must be present, scanned clean and not rejected before a vendor can be approved.</summary>
    public static readonly IReadOnlyList<string> RequiredForApproval = [CommercialRegistration, TaxCertificate];
}

public static class DocumentStatus
{
    public const string Uploaded = "Uploaded";
    public const string Verified = "Verified";
    public const string Rejected = "Rejected";
}

public class VendorDocument : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string Type { get; set; } = "";
    public Guid AttachmentId { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public string Status { get; set; } = DocumentStatus.Uploaded;
    public string? ReviewNote { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public string UploaderKind { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The accounting rules one legal entity applies to a vendor. The same vendor can have different rules per entity.</summary>
public class VendorEntityLink : ILegalEntityScoped, IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid VendorId { get; set; }
    public string? ErpVendorAccount { get; set; }
    public string? PaymentTermsCode { get; set; }
    public string DefaultCurrency { get; set; } = "";
    public string? DefaultTaxCode { get; set; }
    public string? DefaultDimensionsJson { get; set; }
    public string? WithholdingRuleCode { get; set; }
    public string Status { get; set; } = "Active";
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class RequestStatus
{
    public const string Open = "Open";
    public const string Responded = "Responded";
    public const string Closed = "Closed";
}

/// <summary>A question or request from the customer to a vendor (e.g. "send an updated tax certificate").</summary>
public class VendorRequest : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string Subject { get; set; } = "";
    public string Status { get; set; } = RequestStatus.Open;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public static class MessageDirection
{
    public const string ToVendor = "ToVendor";
    public const string FromVendor = "FromVendor";
}

public class VendorMessage : IVendorOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public Guid RequestId { get; set; }
    public string Direction { get; set; } = "";
    public string Body { get; set; } = "";
    public Guid? AuthorId { get; set; }
    public string AuthorKind { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
