using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

/// <summary>
/// Reads secrets from configuration key "Secrets:{ref}". In deployed environments the Key Vault configuration
/// provider populates this section, so nothing is stored in the repository or the database.
/// </summary>
public sealed class ConfigurationSecretResolver(IConfiguration configuration) : ISecretResolver
{
    public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) =>
        Task.FromResult(configuration[$"Secrets:{secretRef}"]);
}
