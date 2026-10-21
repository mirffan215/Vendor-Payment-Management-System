using System.Text;
using System.Security.Cryptography;

namespace Vpms.BuildingBlocks.Security.Encryption;

/// <summary>
/// AES-256-GCM key wrapping with a key from configuration. For development and tests only: in deployed environments the
/// KEK lives in Key Vault and never in configuration.
/// </summary>
public sealed class LocalKeyEncryptionKey : IKeyEncryptionKey
{
    private readonly byte[] _kek;

    public LocalKeyEncryptionKey(string base64Key)
    {
        _kek = Convert.FromBase64String(base64Key);
        if (_kek.Length != 32)
        {
            throw new EncryptionException("The local key-encryption key must be 32 bytes (base64).");
        }

        KeyId = "local:" + Convert.ToHexStringLower(SHA256.HashData(_kek))[..12];
    }

    public string KeyId { get; }

    public Task<byte[]> WrapAsync(byte[] key, CancellationToken ct = default)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[key.Length];
        using var aes = new AesGcm(_kek, 16);
        aes.Encrypt(nonce, key, cipher, tag, Encoding.ASCII.GetBytes(KeyId));
        return Task.FromResult(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public Task<byte[]> UnwrapAsync(byte[] wrapped, string keyId, CancellationToken ct = default)
    {
        if (keyId != KeyId)
        {
            throw new EncryptionException($"Key '{keyId}' is not available to this key-encryption key.");
        }

        if (wrapped.Length < 28)
        {
            throw new EncryptionException("Wrapped key is malformed.");
        }

        var plain = new byte[wrapped.Length - 28];
        try
        {
            using var aes = new AesGcm(_kek, 16);
            aes.Decrypt(wrapped.AsSpan(0, 12), wrapped.AsSpan(28), wrapped.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(KeyId));
        }
        catch (CryptographicException ex)
        {
            throw new EncryptionException("Wrapped key could not be unwrapped.", ex);
        }

        return Task.FromResult(plain);
    }
}
