using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Security.Encryption;

/// <summary>
/// Ciphertext format: <c>v1.{keyVersion}.{base64url(nonce[12] | tag[16] | ciphertext)}</c>. The tenant and column are
/// authenticated additional data, so a value cannot be moved to another tenant, table or column and still decrypt.
/// </summary>
public sealed class FieldProtector(TenantKeyRing keys, ITenantContext tenant) : IFieldProtector
{
    private const string Prefix = "v1";

    public async Task<string?> ProtectAsync(string column, string? plaintext, CancellationToken ct = default)
    {
        if (plaintext is null)
        {
            return null;
        }

        var tenantId = tenant.RequireTenantId();
        var key = await keys.GetActiveAsync(tenantId, KeyPurpose.Data, ct);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key.Material, 16);
        aes.Encrypt(nonce, data, cipher, tag, Aad(tenantId, column));
        CryptographicOperations.ZeroMemory(data);
        return $"{Prefix}.{key.Version}.{Base64Url(nonce.Concat(tag).Concat(cipher).ToArray())}";
    }

    public async Task<string?> UnprotectAsync(string column, string? ciphertext, CancellationToken ct = default)
    {
        if (ciphertext is null)
        {
            return null;
        }

        var (version, blob) = Parse(ciphertext);
        var tenantId = tenant.RequireTenantId();
        var key = await keys.GetAsync(tenantId, KeyPurpose.Data, version, ct);
        if (blob.Length < 28)
        {
            throw new EncryptionException("Protected value is malformed.");
        }

        var plain = new byte[blob.Length - 28];
        try
        {
            using var aes = new AesGcm(key.Material, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, Aad(tenantId, column));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException ex)
        {
            // Wrong tenant, wrong column, wrong key or tampering: all look the same to the caller.
            throw new EncryptionException("Protected value could not be decrypted.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public async Task<string?> BlindIndexAsync(string column, string? normalizedValue, CancellationToken ct = default)
    {
        if (normalizedValue is null)
        {
            return null;
        }

        var tenantId = tenant.RequireTenantId();
        var key = await keys.GetActiveAsync(tenantId, KeyPurpose.Index, ct);
        using var hmac = new HMACSHA256(key.Material);
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(column + "\n" + normalizedValue)));
    }

    public async Task<bool> NeedsReprotectAsync(string? ciphertext, CancellationToken ct = default)
    {
        if (ciphertext is null)
        {
            return false;
        }

        var (version, _) = Parse(ciphertext);
        var active = await keys.GetActiveAsync(tenant.RequireTenantId(), KeyPurpose.Data, ct);
        return version != active.Version;
    }

    public async Task<string?> ReprotectAsync(string column, string? ciphertext, CancellationToken ct = default)
    {
        if (ciphertext is null || !await NeedsReprotectAsync(ciphertext, ct))
        {
            return ciphertext;
        }

        return await ProtectAsync(column, await UnprotectAsync(column, ciphertext, ct), ct);
    }

    private static byte[] Aad(Guid tenantId, string column) => Encoding.UTF8.GetBytes($"{tenantId:N}|{column}");

    private static (int Version, byte[] Blob) Parse(string value)
    {
        var parts = value.Split('.', 3);
        if (parts.Length != 3 || parts[0] != Prefix || !int.TryParse(parts[1], out var version))
        {
            throw new EncryptionException("Protected value has an unknown format.");
        }

        try
        {
            return (version, FromBase64Url(parts[2]));
        }
        catch (FormatException ex)
        {
            throw new EncryptionException("Protected value is malformed.", ex);
        }
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
