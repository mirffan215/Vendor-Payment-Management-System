namespace Vpms.Documents.Contracts;

public static class ScanStatus
{
    public const string Pending = "Pending";
    public const string Clean = "Clean";
    public const string Infected = "Infected";
    public const string Failed = "Failed";
}

public sealed record AttachmentInfo(
    Guid Id, string OwnerType, string OwnerId, Guid? LegalEntityId, string FileName, string ContentType,
    long SizeBytes, string Sha256, string ScanStatus, DateTimeOffset UploadedAt);

public sealed record UploadRequest(
    string OwnerType, string OwnerId, Guid? LegalEntityId, string FileName, Stream Content,
    Guid? UploadedBy, string UploaderKind);

public sealed record AttachmentDownload(Stream Content, string FileName, string ContentType, long SizeBytes);

public static class UploaderKind
{
    public const string Staff = "Staff";
    public const string Vendor = "Vendor";
    public const string System = "System";
}

/// <summary>Stores and serves files for other modules. Callers are responsible for authorising access to the owner.</summary>
public interface IAttachmentService
{
    /// <summary>Validates and stores the file in quarantine (Pending) until the virus scan marks it Clean.</summary>
    Task<AttachmentInfo> UploadAsync(UploadRequest request, CancellationToken ct = default);

    Task<AttachmentInfo> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<AttachmentInfo>> ListForOwnerAsync(string ownerType, string ownerId, CancellationToken ct = default);

    /// <summary>Only Clean files can be downloaded. The download is audited.</summary>
    Task<AttachmentDownload> OpenDownloadAsync(Guid id, CancellationToken ct = default);
}
