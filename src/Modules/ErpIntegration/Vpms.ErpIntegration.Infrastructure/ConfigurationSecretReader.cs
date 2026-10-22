using Microsoft.Extensions.Configuration;
using Vpms.BuildingBlocks.Security;

namespace Vpms.ErpIntegration.Infrastructure;

/// <summary>Reads secrets from the <c>Secrets:{name}</c> configuration section. In production Key Vault is added as a configuration provider with that prefix, so no secret is ever in the repo.</summary>
public sealed class ConfigurationSecretReader(IConfiguration configuration) : ISecretReader
{
    public Task<string?> ReadAsync(string secretRef, CancellationToken ct = default) => Task.FromResult(configuration[$"Secrets:{secretRef}"]);
}
