using Amazon.S3;
using Amazon.S3.Model;
using Vpms.BuildingBlocks.Storage;

namespace Vpms.BuildingBlocks.Cloud;

/// <summary>S3 or S3-compatible backend (AWS, MinIO, Ceph). Objects are write-once.</summary>
public sealed class S3BlobBackend(IAmazonS3 s3, string bucket, bool serverSideEncryption) : IBlobBackend
{
    public async Task PutAsync(string key, Stream content, CancellationToken ct = default)
    {
        // The S3 API needs the length up front; callers have already enforced a size limit before storing.
        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        if (await ExistsAsync(key, ct))
        {
            throw new InvalidOperationException("Storage key already exists; objects are write-once.");
        }

        var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = buffer };
        if (serverSideEncryption)
        {
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256;
        }

        await s3.PutObjectAsync(request, ct);
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, key, ct);
            return new ResponseStream(response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await s3.DeleteObjectAsync(bucket, key, ct);

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Disposes the S3 response together with the stream the caller reads.</summary>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
