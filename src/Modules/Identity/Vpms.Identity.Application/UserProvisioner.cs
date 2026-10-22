using System.Text.Json;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Application;

public sealed record ExternalIdentity(string Subject, string? Email, string? Name);

public sealed record SignedInUser(Guid UserId, Guid TenantId, Guid ProviderId, string Email, string DisplayName, string Locale);

public enum SignInDenial
{
    ProviderDisabled,
    MissingSubject,
    EmailDomainNotAllowed,
    NotProvisioned,
    UserDisabled,

    /// <summary>A step-up sign-in came back without proof that the person authenticated again just now.</summary>
    StepUpNotPerformed,
}

public sealed record SignInResult(SignedInUser? User, SignInDenial? Denial)
{
    public bool Succeeded => User is not null;
}

/// <summary>
/// Maps a validated external identity to a VPMS user. The tenant comes from the provider configuration held by
/// the server, never from claims inside the external token.
/// </summary>
public sealed class UserProvisioner(IIdentityStore store, TenantContext tenant, TimeProvider clock, IAuditPort audit, IRoleAssignmentService roles, Vpms.CustomerAdmin.Contracts.ITenantLookup? tenants = null)
{
    public async Task<SignInResult> SignInAsync(IdentityProviderConfig provider, ExternalIdentity identity,
        CancellationToken ct = default)
    {
        if (!provider.Enabled)
        {
            return await Deny(provider, SignInDenial.ProviderDisabled, ct);
        }

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return await Deny(provider, SignInDenial.MissingSubject, ct);
        }

        tenant.SetTenant(provider.TenantId);

        var user = await store.FindUserAsync(provider.Id, identity.Subject, ct);
        if (user is null)
        {
            if (!provider.AutoProvision)
            {
                return await Deny(provider, SignInDenial.NotProvisioned, ct);
            }

            if (!EmailAllowed(provider, identity.Email))
            {
                return await Deny(provider, SignInDenial.EmailDomainNotAllowed, ct);
            }

            // A new person starts with the organisation's language and calendar, and can change them for themselves.
            var defaults = tenants is null ? null : await tenants.GetDefaultsAsync(provider.TenantId, ct);
            user = new User
            {
                Locale = defaults?.Locale ?? "en",
                Calendar = defaults?.Calendar ?? "gregorian",
                Id = Guid.NewGuid(),
                TenantId = provider.TenantId,
                ProviderId = provider.Id,
                ExternalSubject = identity.Subject,
                Email = identity.Email!,
                DisplayName = string.IsNullOrWhiteSpace(identity.Name) ? identity.Email! : identity.Name,
                CreatedAt = clock.GetUtcNow(),
            };
            await store.AddUserAsync(user, ct);
            audit.Record(new AuditEntry("user.provisioned", "User", user.Id.ToString(), ActorType: AuditActorType.System,
                ChangeSummaryJson: JsonSerializer.Serialize(new { providerId = provider.Id })));
            await store.SaveChangesAsync(ct);
            // Roles prepared for this email (e.g. the first administrator) take effect now; the email is already verified.
            await roles.ApplyPendingAsync(user.Id, user.Email, ct);
        }

        if (user.Status != UserStatus.Active)
        {
            return await Deny(provider, SignInDenial.UserDisabled, ct, user.Id);
        }

        user.LastLoginAt = clock.GetUtcNow();
        audit.Record(new AuditEntry("auth.login.succeeded", "User", user.Id.ToString(), ActorId: user.Id,
            ActorType: AuditActorType.User, ChangeSummaryJson: JsonSerializer.Serialize(new { providerId = provider.Id })));
        await store.SaveChangesAsync(ct);

        return new SignInResult(
            new SignedInUser(user.Id, user.TenantId, user.ProviderId, user.Email, user.DisplayName, user.Locale), null);
    }

    private async Task<SignInResult> Deny(IdentityProviderConfig provider, SignInDenial reason, CancellationToken ct, Guid? userId = null)
    {
        // No personal data in the record: only the provider and the reason.
        await audit.WriteNowAsync(new AuditEntry("auth.login.denied", "User", userId?.ToString(), TenantId: provider.TenantId,
            ActorType: AuditActorType.Anonymous,
            ChangeSummaryJson: JsonSerializer.Serialize(new { providerId = provider.Id, reason = reason.ToString() })), ct);
        return new SignInResult(null, reason);
    }

    public static bool EmailAllowed(IdentityProviderConfig provider, string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        var domain = email[(email.LastIndexOf('@') + 1)..];
        return provider.AllowedEmailDomains
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(domain, StringComparer.OrdinalIgnoreCase);
    }
}
