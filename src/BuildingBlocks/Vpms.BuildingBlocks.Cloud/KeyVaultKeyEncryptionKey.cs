using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Vpms.BuildingBlocks.Security.Encryption;

namespace Vpms.BuildingBlocks.Cloud;

/// <summary>
/// Wraps tenant data keys with a Key Vault key (RSA-OAEP-256 or AES key-wrap, per the vault key type). The key never
/// leaves the vault. Authenticates with the workload's managed identity.
/// </summary>
public sealed class KeyVaultKeyEncryptionKey : IKeyEncryptionKey
{
    private readonly CryptographyClient _client;
    private readonly KeyWrapAlgorithm _algorithm;

    public KeyVaultKeyEncryptionKey(Uri keyId, KeyWrapAlgorithm? algorithm = null)
    {
        _client = new CryptographyClient(keyId, new DefaultAzureCredential());
        _algorithm = algorithm ?? KeyWrapAlgorithm.RsaOaep256;
        KeyId = "kv:" + keyId;
    }

    public string KeyId { get; }

    public async Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default) =>
        (await _client.WrapKeyAsync(_algorithm, key, ct)).EncryptedKey;

    public async Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, CancellationToken ct = default)
    {
        if (keyId != KeyId)
        {
            throw new EncryptionException($"Key '{keyId}' does not match the configured Key Vault key.");
        }

        return (await _client.UnwrapKeyAsync(_algorithm, wrapped, ct)).Key;
    }
}
