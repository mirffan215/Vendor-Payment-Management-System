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
}

public sealed record SignInResult(SignedInUser? User, SignInDenial? Denial)
{
    public bool Succeeded => User is not null;
}

/// <summary>
/// Maps a validated external identity to a VPMS user. The tenant comes from the provider configuration held by
/// the server, never from claims inside the external token.
/// </summary>
public sealed class UserProvisioner(IIdentityStore store, TenantContext tenant, TimeProvider clock)
{
    public async Task<SignInResult> SignInAsync(IdentityProviderConfig provider, ExternalIdentity identity,
        CancellationToken ct = default)
    {
        if (!provider.Enabled)
        {
            return Deny(SignInDenial.ProviderDisabled);
        }

        if (string.IsNullOrWhiteSpace(identity.Subject))
        {
            return Deny(SignInDenial.MissingSubject);
        }

        tenant.SetTenant(provider.TenantId);

        var user = await store.FindUserAsync(provider.Id, identity.Subject, ct);
        if (user is null)
        {
            if (!provider.AutoProvision)
            {
                return Deny(SignInDenial.NotProvisioned);
            }

            if (!EmailAllowed(provider, identity.Email))
            {
                return Deny(SignInDenial.EmailDomainNotAllowed);
            }

            user = new User
            {
                Id = Guid.NewGuid(),
                TenantId = provider.TenantId,
                ProviderId = provider.Id,
                ExternalSubject = identity.Subject,
                Email = identity.Email!,
                DisplayName = string.IsNullOrWhiteSpace(identity.Name) ? identity.Email! : identity.Name,
                CreatedAt = clock.GetUtcNow(),
            };
            await store.AddUserAsync(user, ct);
        }

        if (user.Status != UserStatus.Active)
        {
            return Deny(SignInDenial.UserDisabled);
        }

        user.LastLoginAt = clock.GetUtcNow();
        await store.SaveChangesAsync(ct);

        return new SignInResult(
            new SignedInUser(user.Id, user.TenantId, user.ProviderId, user.Email, user.DisplayName, user.Locale), null);
    }

    private static SignInResult Deny(SignInDenial reason) => new(null, reason);

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
