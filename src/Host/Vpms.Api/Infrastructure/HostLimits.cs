using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace Vpms.Api.Infrastructure;

public sealed class RequestLimitSettings
{
    public const string Section = "Limits";

    /// <summary>Default request body limit. Upload actions raise it for themselves with [RequestSizeLimit].</summary>
    public long MaxRequestBodyBytes { get; set; } = 1_048_576;

    public int MaxRequestHeaderCount { get; set; } = 100;

    public int MaxRequestHeadersTotalBytes { get; set; } = 32_768;

    public int RequestHeadersTimeoutSeconds { get; set; } = 30;

    public int KeepAliveSeconds { get; set; } = 120;

    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>Largest multipart body a form may carry (a file plus its fields).</summary>
    public long MaxMultipartBytes { get; set; } = 26_214_400;

    /// <summary>How many uploads one instance processes at once; uploads are held in memory while they are inspected.</summary>
    public int ConcurrentUploads { get; set; } = 8;
}

public static class HostLimits
{
    public const string UploadPolicy = "uploads";

    public static void ConfigureVpmsKestrel(this IWebHostBuilder web, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RequestLimitSettings.Section).Get<RequestLimitSettings>() ?? new RequestLimitSettings();
        web.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
            k.Limits.MaxRequestHeaderCount = limits.MaxRequestHeaderCount;
            k.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeadersTotalBytes;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(limits.RequestHeadersTimeoutSeconds);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(limits.KeepAliveSeconds);
        });
    }

    public static IServiceCollection AddVpmsRequestLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RequestLimitSettings>().Bind(configuration.GetSection(RequestLimitSettings.Section));
        var limits = configuration.GetSection(RequestLimitSettings.Section).Get<RequestLimitSettings>() ?? new RequestLimitSettings();
        services.Configure<FormOptions>(o =>
        {
            o.MultipartBodyLengthLimit = limits.MaxMultipartBytes;
            o.ValueCountLimit = 200;
            o.KeyLengthLimit = 256;
            o.MultipartHeadersCountLimit = 32;
        });
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.MaxDepth = limits.MaxJsonDepth);
        return services;
    }

    /// <summary>
    /// Behind the ingress the client address and the https scheme arrive in X-Forwarded-* headers. They are honoured only from the proxies named
    /// in configuration (or from any peer when the network guarantees only the ingress can reach the app); with nothing configured they are
    /// ignored, so a caller cannot spoof their address into the rate limiter or the audit trail.
    /// </summary>
    public static IServiceCollection AddVpmsForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("ForwardedHeaders");
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardLimit = section.GetValue("ForwardLimit", 1);
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
            var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
            foreach (var proxy in proxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var cidr in networks) o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));

            // ASP.NET treats empty proxy lists as "trust every sender", so with nothing configured the headers are switched off instead.
            // TrustImmediatePeer is the explicit statement that the network lets only the ingress reach the app.
            var configured = proxies.Length > 0 || networks.Length > 0 || section.GetValue("TrustImmediatePeer", false);
            o.ForwardedHeaders = configured ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost : ForwardedHeaders.None;
        });
        return services;
    }
}
