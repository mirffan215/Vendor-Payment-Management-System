using Vpms.BuildingBlocks.Security;
using Vpms.Identity.Application;

namespace Vpms.Api.Auth;

/// <summary>
/// Identity's view of the one secret source (<see cref="ISecretReader"/>, configuration key "Secrets:{ref}"). In deployed environments the
/// Key Vault configuration provider populates that section, so nothing is stored in the repository or the database.
/// </summary>
public sealed class ConfigurationSecretResolver(ISecretReader reader) : ISecretResolver
{
    public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => reader.ReadAsync(secretRef, ct);
}
