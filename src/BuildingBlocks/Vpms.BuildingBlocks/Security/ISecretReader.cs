namespace Vpms.BuildingBlocks.Security;

/// <summary>
/// Resolves a secret *reference* (a Key Vault secret name) to its value for integrations. Configuration only ever stores references.
/// In production the references resolve through Key Vault wired in as a configuration source.
/// </summary>
public interface ISecretReader
{
    /// <summary>The secret value, or null when the reference is unknown. Callers must never log or return the value.</summary>
    Task<string?> ReadAsync(string secretRef, CancellationToken ct = default);
}
