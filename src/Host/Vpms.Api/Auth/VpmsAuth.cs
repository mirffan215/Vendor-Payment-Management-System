namespace Vpms.Api.Auth;

public static class VpmsAuth
{
    /// <summary>Internal (customer staff) session cookie.</summary>
    public const string CookieScheme = "vpms.internal";

    /// <summary>Reserved for the vendor self-service portal (Phase 2): a separate scheme, cookie and context.</summary>
    public const string VendorCookieScheme = "vpms.vendor";

    public const string InternalPolicy = "Internal";
}

public static class VpmsClaims
{
    public const string UserId = "vpms:uid";
    public const string TenantId = "vpms:tid";
    public const string ProviderId = "vpms:pid";
    public const string Context = "vpms:ctx";
    public const string AuthTime = "vpms:auth_time";
    public const string InternalContext = "internal";
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan AbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Allows http:// OIDC metadata. Development and tests only; must be false in production.</summary>
    public bool AllowInsecureMetadata { get; set; }
}

/// <summary>Test seam: lets integration tests route OIDC back-channel calls to an in-process fake provider.</summary>
public sealed class OidcBackchannelOptions
{
    public HttpMessageHandler? Handler { get; set; }
}
