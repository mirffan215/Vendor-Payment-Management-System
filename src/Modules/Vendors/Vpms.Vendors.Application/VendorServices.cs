namespace Vpms.Vendors.Application;

public sealed record VendorDto(
    Guid Id, string LegalName, string? LegalNameAr, string? TradeName, string CountryCode, string? TaxId, string? RegistrationNo,
    string? ContactEmail, string? ContactPhone, string Status, string VerificationStatus, string? StatusReason,
    DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? ApprovedAt);

public sealed record VendorListItem(Guid Id, string LegalName, string CountryCode, string Status, string VerificationStatus, DateTimeOffset CreatedAt);

public sealed record CreateVendor(
    string LegalName, string? LegalNameAr, string? TradeName, string CountryCode, string? TaxId, string? RegistrationNo,
    string? ContactEmail, string? ContactPhone, bool ConfirmDuplicate = false);

public sealed record UpdateVendor(
    string LegalName, string? LegalNameAr, string? TradeName, string? TaxId, string? RegistrationNo, string? ContactEmail, string? ContactPhone);

public sealed record VendorDocumentDto(
    Guid Id, string Type, Guid AttachmentId, DateOnly? ExpiresOn, string Status, string? ReviewNote, string FileName, string ScanStatus, DateTimeOffset CreatedAt);

public sealed record VendorDetails(VendorDto Vendor, IReadOnlyList<VendorDocumentDto> Documents, IReadOnlyList<VendorEntityLinkDto> Links);

public sealed record VendorEntityLinkDto(
    Guid Id, Guid LegalEntityId, Guid VendorId, string? ErpVendorAccount, string? PaymentTermsCode, string DefaultCurrency,
    string? DefaultTaxCode, string? DefaultDimensionsJson, string? WithholdingRuleCode, string Status);

public sealed record SaveVendorEntityLink(
    string? ErpVendorAccount, string? PaymentTermsCode, string DefaultCurrency, string? DefaultTaxCode,
    string? DefaultDimensionsJson, string? WithholdingRuleCode);

public interface IVendorService
{
    /// <param name="entityScope">null = all vendors; otherwise only vendors linked to these legal entities.</param>
    Task<IReadOnlyList<VendorListItem>> ListAsync(string? status, string? search, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<VendorDetails> GetAsync(Guid id, CancellationToken ct = default);

    Task<VendorDto> CreateAsync(CreateVendor request, CancellationToken ct = default);

    Task<VendorDto> UpdateAsync(Guid id, UpdateVendor request, CancellationToken ct = default);

    Task<VendorDto> SubmitAsync(Guid id, CancellationToken ct = default);

    Task<VendorDto> StartReviewAsync(Guid id, CancellationToken ct = default);

    Task<VendorDto> ApproveAsync(Guid id, CancellationToken ct = default);

    Task<VendorDto> RejectAsync(Guid id, string reason, CancellationToken ct = default);

    Task<VendorDto> ReopenAsync(Guid id, CancellationToken ct = default);

    Task<VendorDto> SuspendAsync(Guid id, string reason, CancellationToken ct = default);

    Task<VendorDto> ReactivateAsync(Guid id, CancellationToken ct = default);
}

public interface IVendorDocumentService
{
    /// <summary>Links an uploaded attachment (owner type "Vendor", owner id = the vendor) to the vendor as a document.</summary>
    Task<VendorDocumentDto> AddAsync(Guid vendorId, string type, Guid attachmentId, DateOnly? expiresOn, CancellationToken ct = default);

    Task<IReadOnlyList<VendorDocumentDto>> ListAsync(Guid vendorId, CancellationToken ct = default);

    Task<VendorDocumentDto> VerifyAsync(Guid documentId, CancellationToken ct = default);

    Task<VendorDocumentDto> RejectAsync(Guid documentId, string note, CancellationToken ct = default);
}

public interface IVendorEntityLinkService
{
    Task<IReadOnlyList<VendorEntityLinkDto>> ListAsync(Guid vendorId, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    Task<VendorEntityLinkDto> SaveAsync(Guid vendorId, Guid legalEntityId, SaveVendorEntityLink request, CancellationToken ct = default);

    Task<VendorEntityLinkDto> SetBlockedAsync(Guid vendorId, Guid legalEntityId, bool blocked, CancellationToken ct = default);
}

public sealed record RequestDto(Guid Id, Guid VendorId, string Subject, string Status, DateTimeOffset CreatedAt, IReadOnlyList<MessageDto> Messages);

public sealed record MessageDto(Guid Id, string Direction, string Body, string AuthorKind, DateTimeOffset CreatedAt);

public interface IVendorRequestService
{
    Task<RequestDto> CreateAsync(Guid vendorId, string subject, string body, CancellationToken ct = default);

    Task<IReadOnlyList<RequestDto>> ListAsync(Guid vendorId, CancellationToken ct = default);

    Task<RequestDto> ReplyAsync(Guid requestId, string body, CancellationToken ct = default);

    Task<RequestDto> CloseAsync(Guid requestId, CancellationToken ct = default);
}

public static class VendorValidation
{
    public static string NormalizeId(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
