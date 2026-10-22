using System.ComponentModel.DataAnnotations;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.VendorPortal.Domain;

public static class PortalUserStatus
{
    public const string Invited = "Invited";
    public const string Active = "Active";
    public const string Disabled = "Disabled";
}

public static class InvitePurpose
{
    /// <summary>First access: set a password and enrol an authenticator.</summary>
    public const string Invite = "Invite";

    /// <summary>Forgotten password: set a new one; the existing authenticator stays.</summary>
    public const string PasswordReset = "PasswordReset";
}

/// <summary>A person at a vendor who may use the self-service portal. Password and authenticator are both required.</summary>
public class VendorUser : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorId { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string? TotpSecretCipher { get; set; }
    public DateTimeOffset? TotpEnrolledAt { get; set; }
    public long LastTotpStep { get; set; }
    public string Status { get; set; } = PortalUserStatus.Invited;
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public Guid? MfaResetRequestedBy { get; set; }
    public DateTimeOffset? MfaResetRequestedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public bool MfaEnrolled => TotpEnrolledAt is not null;

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Counts a failed password or authenticator attempt; returns true when this attempt locked the account.</summary>
    public bool RegisterFailure(DateTimeOffset now, int maxAttempts, TimeSpan lockout)
    {
        FailedAttempts++;
        if (FailedAttempts < maxAttempts) return false;
        LockedUntil = now + lockout;
        FailedAttempts = 0;
        return true;
    }

    public void RegisterSuccess(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }
}

public class VendorInvite : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorUserId { get; set; }

    /// <summary>SHA-256 of the token. The token itself is shown once and never stored.</summary>
    public string TokenHash { get; set; } = "";
    public string Purpose { get; set; } = InvitePurpose.Invite;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid IssuedBy { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;
}

public class VendorRecoveryCode : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VendorUserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset? UsedAt { get; set; }
}
