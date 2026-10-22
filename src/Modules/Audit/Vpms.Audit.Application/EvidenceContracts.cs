namespace Vpms.Audit.Application;

public sealed record EvidenceBundle(byte[] Content, string FileName, int Events, string ManifestSha256, bool ChainValid);

public sealed record EvidenceVerification(
    bool IsValid, int EventsChecked, bool SignatureValid, bool FilesIntact, bool EventsIntact, bool ChainLinked, IReadOnlyList<string> Problems);

/// <summary>
/// Evidence for an auditor: the filtered events with everything needed to check them, the state of the tenant's hash chain and its external
/// anchors at the time, and a signed manifest of file hashes. Nothing in a bundle is secret beyond what the audit trail already shows its reader.
/// </summary>
public interface IAuditEvidenceService
{
    public const int MaxEvents = 50_000;

    /// <exception cref="Vpms.BuildingBlocks.Errors.ValidationFailedException">More than <see cref="MaxEvents"/> events match; narrow the filter.</exception>
    Task<EvidenceBundle> BuildAsync(AuditQuery filter, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default);

    /// <summary>Checks a bundle produced for the current tenant: signature, file hashes, each event's own hash, and links between consecutive events.</summary>
    Task<EvidenceVerification> VerifyAsync(Stream bundle, CancellationToken ct = default);
}
