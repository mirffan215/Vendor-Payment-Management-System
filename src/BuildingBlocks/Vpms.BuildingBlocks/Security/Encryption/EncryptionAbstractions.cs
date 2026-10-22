namespace Vpms.BuildingBlocks.Security.Encryption;

public static class KeyPurpose
{
    /// <summary>AES-256-GCM key that protects field values.</summary>
    public const string Data = "data";

    /// <summary>HMAC-SHA-256 key for blind indexes (searchable, non-reversible).</summary>
    public const string Index = "index";
}

public static class KeyStatus
{
    public const string Active = "Active";
    public const string Retired = "Retired";

    /// <summary>The wrapped key was deleted when the tenant left (ADR-0024). The row stays as a tombstone so a key can never be silently created again.</summary>
    public const string Destroyed = "Destroyed";
}

/// <summary>A tenant key as stored: only ever wrapped by the key-encryption key.</summary>
public sealed record StoredTenantKey(Guid TenantId, string Purpose, int Version, string Status, string KekId, byte[] WrappedKey);

/// <summary>Persistence for wrapped tenant keys. Implemented by the module that owns tenants.</summary>
public interface ITenantKeyStore
{
    Task<IReadOnlyList<StoredTenantKey>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Adds a key version and, in the same transaction, retires <paramref name="retireVersion"/> of the same purpose.
    /// Writes the matching audit record. Returns false when that version already exists (a concurrent creator won).
    /// </summary>
    Task<bool> TryAddAsync(StoredTenantKey key, int? retireVersion, CancellationToken ct = default);
}

/// <summary>Wraps data keys with a key that never leaves the key store (Key Vault in deployed environments).</summary>
public interface IKeyEncryptionKey
{
    string KeyId { get; }

    Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default);

    Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, CancellationToken ct = default);
}

public sealed class EncryptionOptions
{
    public const string Section = "Encryption";

    /// <summary>"Local" (development and tests only) or "KeyVault".</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>Base64 32-byte key for the Local provider. Never use outside development.</summary>
    public string? LocalKek { get; set; }

    /// <summary>Key Vault key identifier (https://vault/keys/name[/version]) for the KeyVault provider.</summary>
    public string? KeyVaultKeyId { get; set; }

    public TimeSpan KeyCacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}

public class EncryptionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The tenant's keys were destroyed when it left, so its encrypted data can no longer be read (and no new key is created).</summary>
public sealed class TenantKeyDestroyedException(Guid tenantId) : EncryptionException($"The keys of tenant {tenantId:N} have been destroyed.");

/// <summary>Protects sensitive column values for the current tenant.</summary>
public interface IFieldProtector
{
    /// <summary>Encrypts a value; <paramref name="column"/> (e.g. "vendor.BankDetails.Iban") is bound into the ciphertext.</summary>
    Task<string?> ProtectAsync(string column, string? plaintext, CancellationToken ct = default);

    Task<string?> UnprotectAsync(string column, string? ciphertext, CancellationToken ct = default);

    /// <summary>Deterministic keyed hash for equality search and duplicate detection. Callers pass a normalised value.</summary>
    Task<string?> BlindIndexAsync(string column, string? normalizedValue, CancellationToken ct = default);

    /// <summary>True when the value is protected with a key version older than the active one.</summary>
    Task<bool> NeedsReprotectAsync(string? ciphertext, CancellationToken ct = default);

    /// <summary>Re-encrypts under the active key (no-op result equals input when already current).</summary>
    Task<string?> ReprotectAsync(string column, string? ciphertext, CancellationToken ct = default);
}

/// <summary>Creates key versions; rotation retires the previous active key but keeps it for decryption.</summary>
public interface ITenantKeyManager
{
    Task<int> RotateDataKeyAsync(Guid tenantId, CancellationToken ct = default);
}
