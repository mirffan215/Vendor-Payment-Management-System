namespace Vpms.VendorPortal.Application;

public sealed class PortalOptions
{
    public const string Section = "Portal";

    /// <summary>Base URL of the web app, used to build invitation links.</summary>
    public string PublicBaseUrl { get; set; } = "https://localhost";

    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);
    public int MaxFailedAttempts { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int PasswordHashIterations { get; set; } = 600_000;
    public string Issuer { get; set; } = "VPMS";

    /// <summary>A vendor session ends after this long without activity.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>A vendor session ends this long after sign-in, however active it is.</summary>
    public TimeSpan SessionAbsoluteTimeout { get; set; } = TimeSpan.FromHours(8);
}

public sealed record PortalUserDto(
    Guid Id, Guid VendorId, string Email, string DisplayName, string Status, bool MfaEnrolled, DateTimeOffset? LastLoginAt, bool Locked,
    bool MfaResetPending);

public sealed record InviteResult(PortalUserDto User, string InviteUrl, DateTimeOffset ExpiresAt);

public interface IPortalAdminService
{
    Task<IReadOnlyList<PortalUserDto>> ListUsersAsync(Guid vendorId, CancellationToken ct = default);

    Task<InviteResult> InviteUserAsync(Guid vendorId, string email, string displayName, CancellationToken ct = default);

    /// <summary>Issues a fresh link (revoking earlier ones). For people who already finished setup it is a password reset.</summary>
    Task<InviteResult> ReissueInviteAsync(Guid userId, CancellationToken ct = default);

    Task<PortalUserDto> SetStatusAsync(Guid userId, bool enabled, CancellationToken ct = default);

    /// <summary>Step 1 of removing someone's authenticator (lost phone). A different staff member must approve.</summary>
    Task<PortalUserDto> RequestMfaResetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Step 2: clears the authenticator, password and recovery codes and issues a new setup link.</summary>
    Task<InviteResult> ApproveMfaResetAsync(Guid userId, CancellationToken ct = default);
}

public sealed record InviteInfo(string Email, string DisplayName, string VendorName, string Purpose, bool NeedsAuthenticator);

public sealed record Enrollment(string Secret, string ProvisioningUri);

public sealed record AcceptResult(bool Completed, Enrollment? Enrollment);

/// <summary>What the API needs to start a session. Nothing here comes from the client.</summary>
public sealed record PortalSession(Guid UserId, Guid TenantId, Guid VendorId, string DisplayName, string Email);

public sealed record EnrollmentResult(PortalSession Session, IReadOnlyList<string> RecoveryCodes);

public sealed record LoginChallenge(string Challenge);

/// <summary>Any failed sign-in step. The message and code are deliberately identical for every cause.</summary>
public sealed class InvalidCredentialsException() : Exception("Sign-in failed. Check your details and try again.");

public interface IPortalAuthService
{
    Task<InviteInfo> GetInviteAsync(string tenantSlug, string token, CancellationToken ct = default);

    Task<AcceptResult> AcceptInviteAsync(string tenantSlug, string token, string password, CancellationToken ct = default);

    Task<EnrollmentResult> CompleteEnrollmentAsync(string tenantSlug, string token, string code, CancellationToken ct = default);

    /// <summary>Step 1 of sign-in. Returns a short-lived challenge to present with the authenticator code.</summary>
    Task<LoginChallenge> LoginAsync(string tenantSlug, string email, string password, CancellationToken ct = default);

    /// <summary>Step 2: an authenticator code or a recovery code.</summary>
    Task<PortalSession> VerifyMfaAsync(string challenge, string codeOrRecoveryCode, CancellationToken ct = default);

    /// <summary>Re-checks that a session's user may still sign in (called on every request, cached briefly).</summary>
    Task<bool> IsSessionValidAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Hands an invitation to the person. Email delivery arrives with the notifications phase; staff pass links on until then.</summary>
public interface IInviteDelivery
{
    Task DeliverAsync(string email, string displayName, string link, DateTimeOffset expiresAt, CancellationToken ct = default);
}
