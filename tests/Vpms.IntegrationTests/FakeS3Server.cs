using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vpms.IntegrationTests;

/// <summary>
/// A tiny path-style S3 endpoint (PUT/GET/HEAD/DELETE object) on a local port. It does not check signatures; it lets the
/// S3 adapter's request construction be tested where MinIO cannot run. The real-store contract runs against MinIO in CI.
/// </summary>
public sealed class FakeS3Server : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public string Url { get; }

    public IReadOnlyDictionary<string, string> LastPutHeaders { get; private set; } = new Dictionary<string, string>();

    private FakeS3Server(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public static async Task<FakeS3Server> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeS3Server? self = null;

        app.Map("/{bucket}/{**key}", async (HttpContext ctx) =>
        {
            var id = $"{ctx.Request.RouteValues["bucket"]}/{ctx.Request.RouteValues["key"]}";
            var store = self!._objects;
            if (ctx.Request.Method == "GET" && ctx.Request.Query.ContainsKey("list-type"))
            {
                // ListObjectsV2 (one page): keys of this bucket under the prefix, in binary order like S3.
                var bucket = ctx.Request.RouteValues["bucket"]?.ToString() ?? "";
                var prefix = ctx.Request.Query["prefix"].ToString();
                var keys = store.Keys.Where(k => k.StartsWith(bucket + "/", StringComparison.Ordinal)).Select(k => k[(bucket.Length + 1)..])
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var xml = new System.Text.StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
                xml.Append(System.Security.SecurityElement.Escape(bucket) is { } name ? $"<Name>{name}</Name>" : "");
                xml.Append($"<Prefix>{System.Security.SecurityElement.Escape(prefix)}</Prefix><KeyCount>{keys.Count}</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>");
                foreach (var k in keys)
                {
                    xml.Append($"<Contents><Key>{System.Security.SecurityElement.Escape(k)}</Key><LastModified>2026-01-01T00:00:00.000Z</LastModified><ETag>{System.Security.SecurityElement.Escape(ETag(store[bucket + "/" + k]))}</ETag><Size>{store[bucket + "/" + k].Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
                }

                xml.Append("</ListBucketResult>");
                ctx.Response.ContentType = "application/xml";
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync(xml.ToString());
                return;
            }

            switch (ctx.Request.Method)
            {
                case "PUT":
                    self.LastPutHeaders = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
                    using (var ms = new MemoryStream())
                    {
                        await ctx.Request.Body.CopyToAsync(ms);
                        var raw = ms.ToArray();
                        // Over plain HTTP the SDK uses aws-chunked framing; real S3 and MinIO unwrap it.
                        var chunked = ctx.Request.Headers["x-amz-content-sha256"].ToString().StartsWith("STREAMING", StringComparison.Ordinal)
                                      || ctx.Request.Headers.ContentEncoding.ToString().Contains("aws-chunked", StringComparison.Ordinal);
                        store[id] = chunked ? DecodeAwsChunked(raw) : raw;
                    }

                    ctx.Response.Headers.ETag = ETag(store[id]);
                    ctx.Response.StatusCode = 200;
                    break;
                case "GET" or "HEAD" when store.TryGetValue(id, out var data):
                    ctx.Response.ContentLength = data.Length;
                    ctx.Response.Headers.ETag = ETag(data);
                    ctx.Response.StatusCode = 200;
                    if (ctx.Request.Method == "GET")
                    {
                        await ctx.Response.Body.WriteAsync(data);
                    }

                    break;
                case "GET" or "HEAD":
                    ctx.Response.StatusCode = 404;
                    if (ctx.Request.Method == "GET")
                    {
                        ctx.Response.ContentType = "application/xml";
                        await ctx.Response.WriteAsync("<Error><Code>NoSuchKey</Code><Message>missing</Message></Error>");
                    }

                    break;
                case "DELETE":
                    store.TryRemove(id, out _);
                    ctx.Response.StatusCode = 204;
                    break;
                default:
                    ctx.Response.StatusCode = 405;
                    break;
            }
        });

        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeS3Server(app, url);
        return self;
    }

    private static byte[] DecodeAwsChunked(byte[] raw)
    {
        var output = new MemoryStream();
        var pos = 0;
        while (pos < raw.Length)
        {
            var lineEnd = Array.IndexOf(raw, (byte)'\r', pos);
            var header = System.Text.Encoding.ASCII.GetString(raw, pos, lineEnd - pos);
            var size = Convert.ToInt32(header.Split(';')[0], 16);
            pos = lineEnd + 2;
            if (size == 0)
            {
                break;
            }

            output.Write(raw, pos, size);
            pos += size + 2;
        }

        return output.ToArray();
    }

    // Real S3 returns the MD5 of the object as its ETag and the SDK checks it.
    private static string ETag(byte[] data) => $"\"{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data))}\"";

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
