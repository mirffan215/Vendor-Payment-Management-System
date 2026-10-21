using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Documents.Domain;

public class Attachment : ILegalEntityScopedOptional
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public string OwnerType { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ScanStatus { get; set; } = Contracts.ScanStatus.Pending;
    public int ScanAttempts { get; set; }
    public string? ScanSignature { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public string UploaderKind { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>Tenant-owned row that may or may not belong to a legal entity.</summary>
public interface ILegalEntityScopedOptional : ITenantOwned
{
    Guid? LegalEntityId { get; set; }
}
