using Vpms.Payments.Domain;

namespace Vpms.Payments.Application;

public sealed record ProposalItemDto(Guid PaymentRequestId, Guid InvoiceId, string InvoiceNumber, decimal Amount);

public sealed record ProposalDto(
    Guid Id, Guid LegalEntityId, string ProposalNo, Guid VendorId, string VendorName, DateOnly DueDate, string CurrencyCode, decimal TotalAmount, string Status,
    string? StatusReason, Guid CreatedBy, DateTimeOffset CreatedAt, Guid? SubmittedBy, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, string? ApprovalComment,
    Guid? ReleasedBy, DateTimeOffset? ReleasedAt, IReadOnlyList<ProposalItemDto> Items);

/// <param name="RequestIds">Pick exact requests (they must share entity, vendor, currency and due date groups; one proposal is made per group). Empty means every available request that matches the other filters.</param>
public sealed record CreateProposals(Guid LegalEntityId, DateOnly? DueOnOrBefore, string? CurrencyCode, Guid? VendorId, IReadOnlyList<Guid>? RequestIds);

public sealed record ProposalFilter(string? Status, Guid? LegalEntityId);

public sealed record RecheckDto(bool Passed, bool Invalidates, IReadOnlyList<RecheckFinding> Findings);

public interface IPaymentProposalService
{
    Task<IReadOnlyList<ProposalDto>> ListAsync(ProposalFilter filter, IReadOnlySet<Guid>? entityScope, int skip, int take, CancellationToken ct = default);

    Task<ProposalDto> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Groups available payment requests into one draft proposal per vendor, due date and currency.</summary>
    Task<IReadOnlyList<ProposalDto>> CreateAsync(CreateProposals request, CancellationToken ct = default);

    /// <summary>Takes one request out of a draft proposal; it becomes available again.</summary>
    Task<ProposalDto> RemoveRequestAsync(Guid proposalId, Guid paymentRequestId, CancellationToken ct = default);

    Task<ProposalDto> SubmitAsync(Guid id, CancellationToken ct = default);

    /// <summary>Treasury approval. Re-checks everything first; a different person from the maker and submitter is required.</summary>
    Task<ProposalDto> ApproveAsync(Guid id, string? comment, CancellationToken ct = default);

    Task<ProposalDto> SendBackAsync(Guid id, string reason, CancellationToken ct = default);

    Task<ProposalDto> CancelAsync(Guid id, string reason, CancellationToken ct = default);

    /// <summary>Runs the checks without changing anything, so a screen can show what would stop approval or release.</summary>
    Task<RecheckDto> PreviewRecheckAsync(Guid id, CancellationToken ct = default);

    Task<Guid> EntityOfAsync(Guid id, CancellationToken ct = default);

    /// <summary>Re-checks open proposals (a safety net behind the eager invalidation). Returns how many were invalidated.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public interface IPaymentScan
{
    Task<IReadOnlyList<Guid>> TenantsWithOpenProposalsAsync(CancellationToken ct = default);
}
