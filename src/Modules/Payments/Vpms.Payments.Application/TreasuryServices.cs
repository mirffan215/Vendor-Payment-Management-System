using Vpms.Payments.Domain;

namespace Vpms.Payments.Application;

public sealed record InstructionDto(
    Guid Id, Guid ProposalId, string ProposalNo, string Format, string Hash, string Signature, int ItemCount, decimal TotalAmount, string CurrencyCode, string Status,
    Guid PreparedBy, DateTimeOffset PreparedAt, Guid? HandedOffBy, DateTimeOffset? HandedOffAt);

public sealed record AuthorizationDto(
    Guid Id, Guid ProposalId, Guid AuthorizedBy, DateTimeOffset AuthorizedAt, string Decision, string? DenialReason, IReadOnlyList<RecheckFinding> Recheck,
    IReadOnlyList<SeparationFinding> Separation, Guid? InstructionId);

public sealed record ReleaseResult(ProposalDto Proposal, InstructionDto Instruction);

public sealed record InstructionFile(string FileName, string ContentType, byte[] Content);

public sealed record InstructionVerification(bool HashMatches, bool SignatureMatches)
{
    public bool Valid => HashMatches && SignatureMatches;
}

/// <summary>What a payment instruction says: who is to be paid how much, for which invoices. Built at release, never stored in clear.</summary>
public sealed record InstructionModel(
    Guid InstructionId, string ProposalNo, Guid LegalEntityId, string LegalEntityName, string CurrencyCode, decimal TotalAmount, DateTimeOffset PreparedAt, Guid ReleasedBy,
    IReadOnlyList<InstructionPayment> Payments);

public sealed record InstructionPayment(
    string Reference, string BeneficiaryName, string BankName, string? SwiftBic, string CountryCode, string? Iban, string? AccountNumber, string CurrencyCode, decimal Amount,
    string InvoiceNumber, DateOnly DueDate);

/// <summary>Turns the model into the files handed to the bank side. New bank formats (pain.001 and so on) are new implementations; nothing else changes.</summary>
public interface IInstructionExporter
{
    string Format { get; }

    (string Csv, string Json) Export(InstructionModel model);
}

public interface ITreasuryService
{
    /// <summary>
    /// Releases an approved proposal: separation of duties, then the full re-check, then the instruction is prepared and signed. Every
    /// attempt, allowed or refused, is recorded. Nothing is sent to a bank.
    /// </summary>
    Task<ReleaseResult> ReleaseAsync(Guid proposalId, CancellationToken ct = default);

    Task<InstructionDto?> GetInstructionAsync(Guid proposalId, CancellationToken ct = default);

    Task<IReadOnlyList<AuthorizationDto>> AuthorizationsAsync(Guid proposalId, CancellationToken ct = default);

    /// <summary>Decrypts one of the files, checks it against its hash and signature, records the download and the first hand-off.</summary>
    Task<InstructionFile> DownloadAsync(Guid instructionId, string kind, CancellationToken ct = default);

    Task<InstructionVerification> VerifyAsync(Guid instructionId, CancellationToken ct = default);

    Task<Guid> EntityOfInstructionAsync(Guid instructionId, CancellationToken ct = default);
}
