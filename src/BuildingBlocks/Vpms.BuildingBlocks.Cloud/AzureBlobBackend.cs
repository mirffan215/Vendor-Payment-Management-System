using Azure;
using Azure.Storage.Blobs;
using Vpms.BuildingBlocks.Storage;

namespace Vpms.BuildingBlocks.Cloud;

/// <summary>Azure Blob Storage backend. Objects are write-once: a key can never be overwritten.</summary>
public sealed class AzureBlobBackend(BlobContainerClient container) : IBlobBackend
{
    public async Task PutAsync(string key, Stream content, CancellationToken ct = default) =>
        await container.GetBlobClient(key).UploadAsync(content, overwrite: false, ct);

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: ct);
            return result.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct);
}
