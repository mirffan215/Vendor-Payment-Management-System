using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Vpms.Api.Auth;

namespace Vpms.Api.Infrastructure;

public sealed class RateLimitSettings
{
    public const string Section = "RateLimiting";

    /// <summary>Requests per window for each user (or client address when anonymous).</summary>
    public int PermitLimit { get; set; } = 600;

    /// <summary>Stricter budget per client address for sign-in endpoints.</summary>
    public int AuthPermitLimit { get; set; } = 30;

    /// <summary>Budget per client address for vendor sign-in, invitation and authenticator endpoints.</summary>
    public int PortalAuthPermitLimit { get; set; } = 20;

    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string PortalAuthPolicy = "portal-auth";
    public const string UploadPolicy = HostLimits.UploadPolicy;

    public static IServiceCollection AddVpmsRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitSettings>().BindConfiguration(RateLimitSettings.Section);
        services.AddRateLimiter();
        // Configured through IOptions so host configuration (and tests) can change the limits.
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitSettings>, IOptions<RequestLimitSettings>>((options, settings, uploadSlots) =>
        {
            var s = settings.Value;
            var window = TimeSpan.FromSeconds(s.WindowSeconds);
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.User.FindFirst(VpmsClaims.UserId)?.Value ?? "ip:" + http.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = s.PermitLimit, Window = window, QueueLimit = 0 }));

            options.AddPolicy(AuthPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                "auth:" + http.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = s.AuthPermitLimit, Window = window, QueueLimit = 0 }));

            options.AddPolicy(PortalAuthPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                "portal:" + http.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = s.PortalAuthPermitLimit, Window = window, QueueLimit = 0 }));

            // Uploads are held in memory while they are inspected, so an instance only works on a few at a time.
            options.AddPolicy(UploadPolicy, http => RateLimitPartition.GetConcurrencyLimiter(
                "uploads",
                _ => new ConcurrencyLimiterOptions { PermitLimit = uploadSlots.Value.ConcurrentUploads, QueueLimit = 0 }));

            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too many requests.",
                    },
                });
            };
        });
        return services;
    }
}
