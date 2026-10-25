using Amazon.Runtime;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Storage;

namespace Vpms.BuildingBlocks.Cloud;

public static class CloudServiceCollectionExtensions
{
    /// <summary>Registers the configured blob backend and the tenant-scoped file store that every caller must use.</summary>
    public static IServiceCollection AddBlobStorage(this IServiceCollection services)
    {
        services.AddOptions<StorageOptions>();
        services.AddSingleton<IBlobBackend>(sp => CreateBackend(sp.GetRequiredService<IOptions<StorageOptions>>().Value));
        services.AddScoped<IFileStore, TenantScopedFileStore>();
        return services;
    }

    /// <summary>
    /// The storage backend for the configured provider. <paramref name="containerOrBucket"/> names another container (Azure) or bucket (S3) in the same
    /// account, used for data that must live apart from the files, such as the audit anchors.
    /// </summary>
    public static IBlobBackend CreateBackend(StorageOptions o, string? containerOrBucket = null) => o.Provider switch
    {
        "Azure" => CreateAzure(containerOrBucket is null ? o.Azure : new AzureStorageOptions { ConnectionString = o.Azure.ConnectionString, AccountUrl = o.Azure.AnchorAccountUrl ?? o.Azure.AccountUrl, Container = containerOrBucket }),
        "S3" => CreateS3(containerOrBucket is null ? o.S3 : new S3StorageOptions
        {
            ServiceUrl = o.S3.ServiceUrl, Bucket = containerOrBucket, Region = o.S3.Region, AccessKey = o.S3.AccessKey, SecretKey = o.S3.SecretKey,
            ForcePathStyle = o.S3.ForcePathStyle, ServerSideEncryption = o.S3.ServerSideEncryption,
        }),
        "InMemory" => new InMemoryBlobBackend(),
        _ => throw new InvalidOperationException($"Unknown storage provider '{o.Provider}'."),
    };

    /// <summary>The backend for the audit anchors: another container (or bucket) of the configured store, named by <c>Storage:AnchorContainer</c>.</summary>
    public static IServiceCollection AddAnchorBackend(this IServiceCollection services, string key)
    {
        services.AddKeyedSingleton<IBlobBackend>(key, (sp, _) =>
        {
            var configuration = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            return CreateBackend(sp.GetRequiredService<IOptions<StorageOptions>>().Value, configuration["Storage:AnchorContainer"] ?? "audit-anchors");
        });
        return services;
    }

    /// <summary>Use Key Vault for tenant key wrapping when Encryption:Provider is "KeyVault".</summary>
    public static IServiceCollection AddKeyVaultKeyEncryption(this IServiceCollection services)
    {
        services.AddSingleton<IKeyEncryptionKey>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
            if (o.Provider == "KeyVault")
            {
                return new KeyVaultKeyEncryptionKey(new Uri(o.KeyVaultKeyId
                    ?? throw new EncryptionException("Encryption:KeyVaultKeyId is required for the KeyVault provider.")));
            }

            return new LocalKeyEncryptionKey(o.LocalKek
                ?? throw new EncryptionException("Encryption:LocalKek is required for the Local provider."));
        });
        return services;
    }

    private static AzureBlobBackend CreateAzure(AzureStorageOptions o)
    {
        BlobContainerClient container = !string.IsNullOrEmpty(o.ConnectionString)
            ? new BlobContainerClient(o.ConnectionString, o.Container)
            : new BlobContainerClient(
                new Uri($"{(o.AccountUrl ?? throw new InvalidOperationException("Storage:Azure:AccountUrl is required.")).TrimEnd('/')}/{o.Container}"),
                new DefaultAzureCredential());
        container.CreateIfNotExists();
        return new AzureBlobBackend(container);
    }

    private static S3BlobBackend CreateS3(S3StorageOptions o)
    {
        var config = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
        if (!string.IsNullOrEmpty(o.ServiceUrl))
        {
            config.ServiceURL = o.ServiceUrl;
            config.AuthenticationRegion = o.Region;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        }

        IAmazonS3 client = !string.IsNullOrEmpty(o.AccessKey)
            ? new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config)
            : new AmazonS3Client(config); // default credential chain (IAM role, workload identity)
        return new S3BlobBackend(client, o.Bucket, o.ServerSideEncryption);
    }
}
