using Vpms.Documents.Contracts;

namespace Vpms.Vendors.Application;

public sealed record PortalProfile(
    Guid Id, string LegalName, string? LegalNameAr, string? TradeName, string CountryCode, string? ContactEmail, string? ContactPhone,
    string Status, string VerificationStatus);

/// <summary>The only vendor fields a vendor may change itself. Legal name, tax and registration numbers go through the customer.</summary>
public sealed record UpdatePortalProfile(string? LegalNameAr, string? TradeName, string? ContactEmail, string? ContactPhone);

public sealed record PortalEntityLink(Guid LegalEntityId, string? LegalEntityName, string? PaymentTermsCode, string DefaultCurrency, string Status);

/// <summary>What a vendor sees of its own bank-detail request: no reviewer identity, comments or callback references.</summary>
public sealed record PortalBankChange(
    Guid Id, string Status, string Reason, DateTimeOffset MadeAt, DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt, BankDetailsDto Proposed);

/// <summary>
/// Everything a signed-in vendor user may do. No method takes a vendor id: the vendor is the one in the session, and the
/// data layer additionally filters every vendor-owned table to it.
/// </summary>
public interface IVendorPortalService
{
    Task<PortalProfile> GetProfileAsync(CancellationToken ct = default);

    Task<PortalProfile> UpdateProfileAsync(UpdatePortalProfile request, CancellationToken ct = default);

    Task<IReadOnlyList<PortalEntityLink>> ListEntityLinksAsync(CancellationToken ct = default);

    Task<IReadOnlyList<VendorDocumentDto>> ListDocumentsAsync(CancellationToken ct = default);

    Task<VendorDocumentDto> UploadDocumentAsync(string type, DateOnly? expiresOn, string fileName, Stream content, CancellationToken ct = default);

    Task<AttachmentDownload> OpenDocumentAsync(Guid documentId, CancellationToken ct = default);

    Task<IReadOnlyList<BankDetailsDto>> ListBankDetailsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PortalBankChange>> ListBankChangesAsync(CancellationToken ct = default);

    /// <summary>Raises a change request. It only takes effect after staff approve it with a callback to the vendor.</summary>
    Task<PortalBankChange> ProposeBankDetailsAsync(ProposeBankDetails request, CancellationToken ct = default);

    Task<PortalBankChange> CancelBankChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<IReadOnlyList<RequestDto>> ListRequestsAsync(CancellationToken ct = default);

    Task<RequestDto> ReplyToRequestAsync(Guid requestId, string body, CancellationToken ct = default);
}
