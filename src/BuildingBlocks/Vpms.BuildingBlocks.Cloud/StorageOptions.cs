namespace Vpms.BuildingBlocks.Cloud;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"Azure", "S3" or "InMemory" (tests and local experiments only).</summary>
    public string Provider { get; set; } = "InMemory";

    public AzureStorageOptions Azure { get; set; } = new();

    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class AzureStorageOptions
{
    /// <summary>Connection string (Azurite or a key-based account). Prefer <see cref="AccountUrl"/> with managed identity.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>https://account.blob.core.windows.net, authenticated with DefaultAzureCredential.</summary>
    public string? AccountUrl { get; set; }

    public string Container { get; set; } = "vpms";
}

public sealed class S3StorageOptions
{
    /// <summary>Custom endpoint for S3-compatible stores (MinIO, Ceph). Empty for AWS.</summary>
    public string? ServiceUrl { get; set; }

    public string Bucket { get; set; } = "vpms";
    public string Region { get; set; } = "us-east-1";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ForcePathStyle { get; set; }

    /// <summary>Ask the store to encrypt objects at rest (SSE-S3). Disable only for emulators without a KMS.</summary>
    public bool ServerSideEncryption { get; set; } = true;
}
