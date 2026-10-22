using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Errors;
using Vpms.BuildingBlocks.Security;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.Vendors.Contracts;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Domain;

namespace Vpms.VendorPortal.Infrastructure;

public sealed class PortalAdminService(
    PortalDbContext db, IVendorLookup vendors, ITenantContext tenant, ITenantLookup tenants, IInviteDelivery delivery, IOptions<PortalOptions> options,
    IAuditPort audit, ICurrentUser current, TimeProvider clock) : IPortalAdminService
{
    private static readonly Regex Email = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<PortalUserDto>> ListUsersAsync(Guid vendorId, CancellationToken ct = default) =>
        (await db.Users.AsNoTracking().Where(u => u.VendorId == vendorId).OrderBy(u => u.Email).ToListAsync(ct)).Select(u => Map(u, clock.GetUtcNow())).ToList();

    public async Task<InviteResult> InviteUserAsync(Guid vendorId, string email, string displayName, CancellationToken ct = default)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        if (normalized.Length > 320 || !Email.IsMatch(normalized)) throw new ValidationFailedException("invalid_email", "Enter a valid email address.");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200) throw new ValidationFailedException("invalid_name", "The person's name is required.");
        if (await vendors.FindAsync(vendorId, ct) is null) throw new NotFoundException("Vendor");
        if (await db.Users.AnyAsync(u => u.Email == normalized, ct))
        {
            throw new ConflictException("portal_user_exists", "Someone with this email already has portal access.");
        }

        var user = new VendorUser { Id = Guid.NewGuid(), VendorId = vendorId, Email = normalized, DisplayName = displayName.Trim(), CreatedBy = current.UserId, CreatedAt = clock.GetUtcNow() };
        db.Users.Add(user);
        audit.Record(new AuditEntry("portal.user.created", "VendorUser", user.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { vendorId })));
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, InvitePurpose.Invite, ct);
    }

    public async Task<InviteResult> ReissueInviteAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await Load(userId, ct);
        if (user.Status == PortalUserStatus.Disabled) throw new ConflictException("user_disabled", "Enable the user before sending a new link.");
        var purpose = user.MfaEnrolled && user.PasswordHash is not null ? InvitePurpose.PasswordReset : InvitePurpose.Invite;
        return await IssueAsync(user, purpose, ct);
    }

    public async Task<PortalUserDto> SetStatusAsync(Guid userId, bool enabled, CancellationToken ct = default)
    {
        var user = await Load(userId, ct);
        var target = enabled ? (user.MfaEnrolled ? PortalUserStatus.Active : PortalUserStatus.Invited) : PortalUserStatus.Disabled;
        if (user.Status != target)
        {
            audit.Record(new AuditEntry(enabled ? "portal.user.enabled" : "portal.user.disabled", "VendorUser", userId.ToString()));
            user.Status = target;
            await db.SaveChangesAsync(ct);
        }

        return Map(user, clock.GetUtcNow());
    }

    public async Task<PortalUserDto> RequestMfaResetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await Load(userId, ct);
        if (!user.MfaEnrolled) throw new ConflictException("no_authenticator", "This person has no authenticator to reset. Send a new invitation instead.");
        if (user.MfaResetRequestedBy is not null) throw new ConflictException("reset_pending", "A reset has already been requested and awaits approval.");

        user.MfaResetRequestedBy = current.UserId;
        user.MfaResetRequestedAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("portal.mfa-reset.requested", "VendorUser", userId.ToString()));
        await db.SaveChangesAsync(ct);
        return Map(user, clock.GetUtcNow());
    }

    public async Task<InviteResult> ApproveMfaResetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await Load(userId, ct);
        if (user.MfaResetRequestedBy is not { } requester) throw new ConflictException("no_reset_requested", "No reset has been requested.");

        try
        {
            MakerCheckerGuard.EnsureDifferent(requester, current.UserId, "approve an authenticator reset");
        }
        catch (SegregationOfDutiesException ex)
        {
            await audit.WriteNowAsync(new AuditEntry("sod.portal.blocked", "VendorUser", userId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { attempted = "mfa-reset-approve", rule = ex.Rule })), ct);
            throw;
        }

        db.RecoveryCodes.RemoveRange(await db.RecoveryCodes.Where(c => c.VendorUserId == userId).ToListAsync(ct));
        user.TotpSecretCipher = null;
        user.TotpEnrolledAt = null;
        user.LastTotpStep = 0;
        user.PasswordHash = null; // setup starts over: new password and a new authenticator
        user.Status = PortalUserStatus.Invited;
        user.MfaResetRequestedBy = null;
        user.MfaResetRequestedAt = null;
        user.FailedAttempts = 0;
        user.LockedUntil = null;
        audit.Record(new AuditEntry("portal.mfa-reset.approved", "VendorUser", userId.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { requestedBy = requester })));
        await db.SaveChangesAsync(ct);
        return await IssueAsync(user, InvitePurpose.Invite, ct);
    }

    private async Task<InviteResult> IssueAsync(VendorUser user, string purpose, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var old in await db.Invites.Where(i => i.VendorUserId == user.Id && i.UsedAt == null && i.RevokedAt == null).ToListAsync(ct))
        {
            old.RevokedAt = now;
        }

        var token = Tokens.NewInviteToken();
        var invite = new VendorInvite
        {
            Id = Guid.NewGuid(), VendorUserId = user.Id, TokenHash = Tokens.Hash(token), Purpose = purpose, IssuedAt = now,
            ExpiresAt = now + options.Value.InviteLifetime, IssuedBy = current.UserId,
        };
        db.Invites.Add(invite);
        audit.Record(new AuditEntry("portal.invite.issued", "VendorUser", user.Id.ToString(), ChangeSummaryJson: JsonSerializer.Serialize(new { purpose, expires = invite.ExpiresAt })));
        await db.SaveChangesAsync(ct);

        var slug = await SlugAsync(ct);
        var link = $"{options.Value.PublicBaseUrl.TrimEnd('/')}/portal/accept?tenant={Uri.EscapeDataString(slug)}&token={token}";
        await delivery.DeliverAsync(user.Email, user.DisplayName, link, invite.ExpiresAt, ct);
        return new InviteResult(Map(user, now), link, invite.ExpiresAt);
    }

    private async Task<string> SlugAsync(CancellationToken ct)
    {
        // The link carries the tenant's public slug so the sign-in pages can resolve the tenant before anyone is signed in.
        var id = tenant.RequireTenantId();
        return await tenants.FindSlugAsync(id, ct) ?? throw new NotFoundException("Tenant");
    }

    private async Task<VendorUser> Load(Guid id, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new NotFoundException("Portal user");

    private static PortalUserDto Map(VendorUser u, DateTimeOffset now) =>
        new(u.Id, u.VendorId, u.Email, u.DisplayName, u.Status, u.MfaEnrolled, u.LastLoginAt, u.IsLocked(now), u.MfaResetRequestedBy is not null);
}
