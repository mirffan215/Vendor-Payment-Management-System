using Vpms.Identity.Domain;

namespace Vpms.Identity.Application;

public interface IIdentityStore
{
    /// <summary>Looks up a provider before any tenant is known (pre-login). Runs in a platform scope.</summary>
    Task<IdentityProviderConfig?> FindProviderAsync(Guid providerId, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderSummary>> ListEnabledProvidersAsync(Guid tenantId, CancellationToken ct = default);

    // The following run inside an established tenant scope.
    Task<User?> FindUserAsync(Guid providerId, string externalSubject, CancellationToken ct = default);
    Task AddUserAsync(User user, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
    Task<bool> IsUserActiveAsync(Guid userId, CancellationToken ct = default);
}

public sealed record ProviderSummary(Guid Id, string DisplayName, IdentityProviderKind Kind);

/// <summary>Resolves a secret reference (Key Vault name) to its value.</summary>
public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default);
}
