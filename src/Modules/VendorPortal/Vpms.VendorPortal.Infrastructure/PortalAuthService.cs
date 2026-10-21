using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Vendors.Contracts;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Domain;

namespace Vpms.VendorPortal.Infrastructure;

public sealed class PortalAuthService(
    PortalDbContext db, TenantContext tenant, ITenantLookup tenants, IVendorLookup vendors, IFieldProtector protector, IPasswordHasher<VendorUser> hasher,
    IDataProtectionProvider dataProtection, IOptions<PortalOptions> options, IAuditPort audit, TimeProvider clock, DummyPasswordHash dummy) : IPortalAuthService
{
    private const string TotpColumn = "portal.VendorUser.TotpSecret";
    private const int RecoveryCodeCount = 10;
    private PortalOptions Options => options.Value;

    // --- invitations ---------------------------------------------------------------------------------------

    public async Task<InviteInfo> GetInviteAsync(string tenantSlug, string token, CancellationToken ct = default)
    {
        var (invite, user) = await LoadInviteAsync(tenantSlug, token, ct);
        var vendor = await vendors.FindAsync(user.VendorId, ct);
        return new InviteInfo(user.Email, user.DisplayName, vendor?.LegalName ?? "", invite.Purpose, NeedsAuthenticator(invite, user));
    }

    public async Task<AcceptResult> AcceptInviteAsync(string tenantSlug, string token, string password, CancellationToken ct = default)
    {
        var (invite, user) = await LoadInviteAsync(tenantSlug, token, ct);
        var problem = PasswordPolicy.Check(password, user.Email, user.DisplayName);
        if (problem is not null)
        {
            throw new ValidationFailedException("weak_password_" + problem, PasswordMessage(problem));
        }

        user.PasswordHash = hasher.HashPassword(user, password);
        user.FailedAttempts = 0;
        user.LockedUntil = null;

        if (!NeedsAuthenticator(invite, user))
        {
            invite.UsedAt = clock.GetUtcNow();
            audit.Record(Event("portal.password.set", user, new { purpose = invite.Purpose }));
            await db.SaveChangesAsync(ct);
            return new AcceptResult(true, null);
        }

        var secret = user.TotpSecretCipher is null ? Totp.NewSecret() : (await protector.UnprotectAsync(TotpColumn, user.TotpSecretCipher, ct))!;
        user.TotpSecretCipher ??= await protector.ProtectAsync(TotpColumn, secret, ct);
        audit.Record(Event("portal.password.set", user, new { purpose = invite.Purpose }));
        await db.SaveChangesAsync(ct);
        return new AcceptResult(false, new Enrollment(secret, Totp.ProvisioningUri(Options.Issuer, user.Email, secret)));
    }

    public async Task<EnrollmentResult> CompleteEnrollmentAsync(string tenantSlug, string token, string code, CancellationToken ct = default)
    {
        var (invite, user) = await LoadInviteAsync(tenantSlug, token, ct);
        if (user.PasswordHash is null || user.TotpSecretCipher is null || user.MfaEnrolled) throw new NotFoundException("Invitation");
        if (user.IsLocked(clock.GetUtcNow())) throw new NotFoundException("Invitation");

        var secret = (await protector.UnprotectAsync(TotpColumn, user.TotpSecretCipher, ct))!;
        var step = Totp.Verify(secret, code, clock.GetUtcNow(), user.LastTotpStep);
        if (step is null)
        {
            var locked = user.RegisterFailure(clock.GetUtcNow(), Options.MaxFailedAttempts, Options.LockoutDuration);
            audit.Record(Event(locked ? "portal.user.locked" : "portal.enrollment.failed", user, null));
            await db.SaveChangesAsync(ct);
            throw new ValidationFailedException("invalid_code", "That code is not correct. Check the time on your phone and try again.");
        }

        var now = clock.GetUtcNow();
        user.LastTotpStep = step.Value;
        user.TotpEnrolledAt = now;
        user.Status = PortalUserStatus.Active;
        user.RegisterSuccess(now);
        invite.UsedAt = now;

        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(c => c.VendorUserId == user.Id).ToListAsync(ct));
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => Tokens.NewRecoveryCode()).ToList();
        db.RecoveryCodes.AddRange(codes.Select(c => new VendorRecoveryCode { Id = Guid.NewGuid(), VendorUserId = user.Id, CodeHash = Tokens.HashRecoveryCode(Tokens.NormalizeRecoveryCode(c)) }));
        audit.Record(Event("portal.mfa.enrolled", user, null));
        audit.Record(Event("portal.login.succeeded", user, new { method = "enrollment" }));
        await db.SaveChangesAsync(ct);
        return new EnrollmentResult(SessionFor(user), codes);
    }

    // --- sign-in -------------------------------------------------------------------------------------------

    public async Task<LoginChallenge> LoginAsync(string tenantSlug, string email, string password, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var user = await FindUserAsync(tenantSlug, email, ct);

        // Same work whether or not the account exists, so response time does not reveal valid addresses.
        var hash = user?.PasswordHash ?? dummy.Value;
        var result = hasher.VerifyHashedPassword(user ?? DummyUser, hash, password ?? "");
        var passwordOk = result != PasswordVerificationResult.Failed;

        if (user is null)
        {
            throw new InvalidCredentialsException();
        }

        if (user.IsLocked(now) || user.Status != PortalUserStatus.Active || !user.MfaEnrolled || user.PasswordHash is null)
        {
            audit.Record(Event("portal.login.rejected", user, new { reason = user.IsLocked(now) ? "locked" : "not_active" }, anonymous: true));
            await db.SaveChangesAsync(ct);
            throw new InvalidCredentialsException();
        }

        if (!passwordOk)
        {
            var locked = user.RegisterFailure(now, Options.MaxFailedAttempts, Options.LockoutDuration);
            audit.Record(Event(locked ? "portal.user.locked" : "portal.login.failed", user, new { step = "password" }, anonymous: true));
            await db.SaveChangesAsync(ct);
            throw new InvalidCredentialsException();
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password!); // iterations were raised since this hash was made
            await db.SaveChangesAsync(ct);
        }

        return new LoginChallenge(Challenge().Protect(JsonSerializer.Serialize(new ChallengePayload(user.Id, user.TenantId, now + Options.ChallengeLifetime))));
    }

    public async Task<PortalSession> VerifyMfaAsync(string challenge, string codeOrRecoveryCode, CancellationToken ct = default)
    {
        ChallengePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<ChallengePayload>(Challenge().Unprotect(challenge))!;
            if (payload.ExpiresAt <= clock.GetUtcNow()) throw new InvalidCredentialsException();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        {
            throw new InvalidCredentialsException();
        }

        tenant.SetTenant(payload.TenantId);
        var now = clock.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == payload.UserId, ct);
        if (user is null || user.IsLocked(now) || user.Status != PortalUserStatus.Active || !user.MfaEnrolled)
        {
            throw new InvalidCredentialsException();
        }

        var secret = (await protector.UnprotectAsync(TotpColumn, user.TotpSecretCipher, ct))!;
        var step = Totp.Verify(secret, codeOrRecoveryCode, now, user.LastTotpStep);
        string method = "totp";

        if (step is null)
        {
            method = "recovery";
            var normalized = Tokens.NormalizeRecoveryCode(codeOrRecoveryCode ?? "");
            var hashed = Tokens.HashRecoveryCode(normalized);
            var recovery = normalized.Length == 10
                ? await db.RecoveryCodes.FirstOrDefaultAsync(c => c.VendorUserId == user.Id && c.CodeHash == hashed && c.UsedAt == null, ct)
                : null;
            if (recovery is null)
            {
                var locked = user.RegisterFailure(now, Options.MaxFailedAttempts, Options.LockoutDuration);
                audit.Record(Event(locked ? "portal.user.locked" : "portal.login.failed", user, new { step = "mfa" }, anonymous: true));
                await db.SaveChangesAsync(ct);
                throw new InvalidCredentialsException();
            }

            recovery.UsedAt = now;
            audit.Record(Event("portal.recovery-code.used", user, new { remaining = await db.RecoveryCodes.CountAsync(c => c.VendorUserId == user.Id && c.UsedAt == null, ct) - 1 }));
        }
        else
        {
            user.LastTotpStep = step.Value; // this code (and any older one) can never be used again
        }

        user.RegisterSuccess(now);
        audit.Record(Event("portal.login.succeeded", user, new { method }));
        await db.SaveChangesAsync(ct);
        return SessionFor(user);
    }

    public async Task<bool> IsSessionValidAsync(Guid userId, CancellationToken ct = default) =>
        await db.Users.AnyAsync(u => u.Id == userId && u.Status == PortalUserStatus.Active && u.TotpEnrolledAt != null, ct);

    // --- internals -----------------------------------------------------------------------------------------

    private sealed record ChallengePayload(Guid UserId, Guid TenantId, DateTimeOffset ExpiresAt);

    // Integrity-protected by data protection; the expiry is inside the payload so it follows the application clock.
    private IDataProtector Challenge() => dataProtection.CreateProtector("vpms.portal.mfa-challenge");

    private static VendorUser DummyUser { get; } = new();

    private async Task<VendorUser?> FindUserAsync(string tenantSlug, string email, CancellationToken ct)
    {
        var tenantId = await tenants.FindTenantIdBySlugAsync(tenantSlug ?? "", ct);
        if (tenantId is null) return null;
        tenant.SetTenant(tenantId.Value);
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        return await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
    }

    private async Task<(VendorInvite Invite, VendorUser User)> LoadInviteAsync(string tenantSlug, string token, CancellationToken ct)
    {
        var tenantId = await tenants.FindTenantIdBySlugAsync(tenantSlug ?? "", ct);
        if (tenantId is null || string.IsNullOrWhiteSpace(token) || token.Length > 100) throw new NotFoundException("Invitation");
        tenant.SetTenant(tenantId.Value);

        var hash = Tokens.Hash(token);
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invite is null || !invite.IsUsable(clock.GetUtcNow())) throw new NotFoundException("Invitation");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == invite.VendorUserId, ct);
        if (user is null || user.Status == PortalUserStatus.Disabled) throw new NotFoundException("Invitation");
        return (invite, user);
    }

    private static bool NeedsAuthenticator(VendorInvite invite, VendorUser user) =>
        invite.Purpose == InvitePurpose.Invite || !user.MfaEnrolled;

    private PortalSession SessionFor(VendorUser u) => new(u.Id, u.TenantId, u.VendorId, u.DisplayName, u.Email);

    private static AuditEntry Event(string action, VendorUser user, object? summary, bool anonymous = false) =>
        new(action, "VendorUser", user.Id.ToString(), ChangeSummaryJson: summary is null ? null : JsonSerializer.Serialize(summary),
            ActorId: anonymous ? null : user.Id, ActorType: anonymous ? AuditActorType.Anonymous : "Vendor");

    private static string PasswordMessage(string code) => code switch
    {
        "too_short" => $"Use at least {PasswordPolicy.MinLength} characters. A short sentence works well.",
        "too_long" => $"Use at most {PasswordPolicy.MaxLength} characters.",
        "too_common" => "That password is too common. Choose something less predictable.",
        "contains_personal_data" => "The password must not contain your name or email address.",
        _ => "Choose a less repetitive, less predictable password.",
    };
}

/// <summary>A password hash of a random value, computed once, so unknown accounts cost the same time as known ones.</summary>
public sealed class DummyPasswordHash(IPasswordHasher<VendorUser> hasher)
{
    public string Value { get; } = hasher.HashPassword(new VendorUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
}
