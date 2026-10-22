using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Payments.Domain;

public static class ReleaseDecision
{
    public const string Released = "Released";
    public const string Denied = "Denied";
}

/// <summary>
/// One attempt to release a proposal, kept forever: who tried, the result of every re-check and of the separation-of-duties evaluation.
/// Denied attempts are kept too.
/// </summary>
public class ReleaseAuthorization : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid AuthorizedBy { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
    public string Decision { get; set; } = ReleaseDecision.Denied;
    public string? DenialReason { get; set; }
    public string RecheckJson { get; set; } = "[]";
    public string SodJson { get; set; } = "[]";
    public Guid? InstructionId { get; set; }
}

public static class InstructionStatus
{
    public const string Prepared = "Prepared";
    public const string HandedOff = "HandedOff";
}

/// <summary>
/// The prepared, signed-off payment instruction. Contents are encrypted at rest. There is no status for execution: this system hands the
/// file over, it never moves money.
/// </summary>
public class PaymentInstruction : ILegalEntityScoped
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid ProposalId { get; set; }
    public Guid AuthorizationId { get; set; }
    public string Format { get; set; } = "";
    public string CsvCipher { get; set; } = "";
    public string JsonCipher { get; set; } = "";

    /// <summary>SHA-256 over both files, and the tenant-keyed signature of that hash.</summary>
    public string Hash { get; set; } = "";

    public string Signature { get; set; } = "";
    public int ItemCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string CurrencyCode { get; set; } = "";
    public string Status { get; set; } = InstructionStatus.Prepared;
    public Guid PreparedBy { get; set; }
    public DateTimeOffset PreparedAt { get; set; }
    public Guid? HandedOffBy { get; set; }
    public DateTimeOffset? HandedOffAt { get; set; }

    public void HandOff(Guid by, DateTimeOffset now)
    {
        if (Status == InstructionStatus.HandedOff) return;
        Status = InstructionStatus.HandedOff;
        HandedOffBy = by;
        HandedOffAt = now;
    }
}
