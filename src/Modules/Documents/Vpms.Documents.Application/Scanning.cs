namespace Vpms.Documents.Application;

public enum ScanVerdict
{
    Clean,
    Infected,
    Error,
}

public sealed record ScanResult(ScanVerdict Verdict, string? Signature = null, string? Detail = null);

public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default);
}

public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    public long MaxBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>"ClamAv" (production) or "Eicar" (development and tests: detects only the EICAR test string).</summary>
    public string Scanner { get; set; } = "ClamAv";

    public int MaxScanAttempts { get; set; } = 3;

    public ClamAvOptions ClamAv { get; set; } = new();
}

public sealed class ClamAvOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Scans quarantined attachments. Runs in the worker, one attachment at a time inside its tenant.</summary>
public interface IAttachmentScanService
{
    /// <summary>Cross-tenant list of attachments waiting for a scan (platform scope).</summary>
    Task<IReadOnlyList<PendingScan>> ListPendingAsync(int max, CancellationToken ct = default);

    /// <summary>Scans one attachment; the caller has established the tenant.</summary>
    Task ScanAsync(Guid attachmentId, CancellationToken ct = default);
}

public sealed record PendingScan(Guid TenantId, Guid AttachmentId);
