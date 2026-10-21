using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.UnitTests.Security;

public class EncryptionTests : IAsyncDisposable
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private readonly ServiceProvider _root;
    private readonly InMemoryKeyStore _store = new();

    public EncryptionTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddSingleton<ITenantKeyStore>(_store);
        services.AddFieldEncryption();
        services.Configure<EncryptionOptions>(o => o.LocalKek = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        _root = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await _root.DisposeAsync();

    private IFieldProtector For(Guid tenant, out AsyncServiceScope scope)
    {
        scope = _root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        return scope.ServiceProvider.GetRequiredService<IFieldProtector>();
    }

    [Fact]
    public async Task Round_trips_including_unicode_and_empty_values()
    {
        var p = For(TenantA, out _);
        foreach (var value in new[] { "DE89370400440532013000", "", "مؤسسة الأمل ٢٠٢٦", new string('x', 5000) })
        {
            var cipher = await p.ProtectAsync("vendor.BankDetails.Iban", value);
            Assert.StartsWith("v1.1.", cipher);
            Assert.Equal(value, await p.UnprotectAsync("vendor.BankDetails.Iban", cipher));
        }

        Assert.Null(await p.ProtectAsync("c", null));
        Assert.Null(await p.UnprotectAsync("c", null));
    }

    [Fact]
    public async Task The_same_value_encrypts_differently_each_time_and_never_contains_the_plaintext()
    {
        var p = For(TenantA, out _);
        var a = await p.ProtectAsync("c", "SA0380000000608010167519");
        var b = await p.ProtectAsync("c", "SA0380000000608010167519");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("SA03800", a, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_cannot_be_decrypted_by_another_tenant()
    {
        var cipher = await For(TenantA, out _).ProtectAsync("vendor.Vendor.TaxId", "300012345600003");
        await Assert.ThrowsAsync<EncryptionException>(() => For(TenantB, out _).UnprotectAsync("vendor.Vendor.TaxId", cipher));
    }

    [Fact]
    public async Task A_value_cannot_be_moved_to_another_column()
    {
        var p = For(TenantA, out _);
        var cipher = await p.ProtectAsync("vendor.BankDetails.Iban", "SA0380000000608010167519");
        await Assert.ThrowsAsync<EncryptionException>(() => p.UnprotectAsync("vendor.Vendor.TaxId", cipher));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("v2.1.AAAA")]
    [InlineData("v1.x.AAAA")]
    [InlineData("v1.1.!!!!")]
    [InlineData("v1.1.AAAA")]
    public async Task Malformed_values_are_rejected(string bad)
    {
        var p = For(TenantA, out _);
        await Assert.ThrowsAsync<EncryptionException>(() => p.UnprotectAsync("c", bad));
    }

    [Fact]
    public async Task Tampering_with_any_part_is_detected()
    {
        var p = For(TenantA, out _);
        var cipher = (await p.ProtectAsync("c", "secret value"))!;
        var parts = cipher.Split('.');
        var blob = Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/').PadRight(((parts[2].Length + 3) / 4) * 4, '='));
        for (var i = 0; i < blob.Length; i += 7)
        {
            var copy = (byte[])blob.Clone();
            copy[i] ^= 0x01;
            var tampered = $"v1.1.{Convert.ToBase64String(copy).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
            await Assert.ThrowsAsync<EncryptionException>(() => p.UnprotectAsync("c", tampered));
        }
    }

    [Fact]
    public async Task Blind_index_is_deterministic_per_tenant_and_column_and_hides_the_value()
    {
        var a = For(TenantA, out _);
        var b = For(TenantB, out _);
        var i1 = await a.BlindIndexAsync("vendor.Vendor.TaxId", "300012345600003");
        Assert.Equal(i1, await a.BlindIndexAsync("vendor.Vendor.TaxId", "300012345600003"));
        Assert.NotEqual(i1, await a.BlindIndexAsync("vendor.Vendor.TaxId", "300012345600004"));
        Assert.NotEqual(i1, await a.BlindIndexAsync("vendor.Vendor.Registration", "300012345600003"));
        Assert.NotEqual(i1, await b.BlindIndexAsync("vendor.Vendor.TaxId", "300012345600003"));
        Assert.Matches("^[0-9a-f]{64}$", i1);
    }

    [Fact]
    public async Task Rotation_keeps_old_values_readable_and_new_values_use_the_new_key()
    {
        var p = For(TenantA, out _);
        var old = (await p.ProtectAsync("c", "before"))!;
        Assert.StartsWith("v1.1.", old);
        Assert.False(await p.NeedsReprotectAsync(old));

        var version = await _root.GetRequiredService<ITenantKeyManager>().RotateDataKeyAsync(TenantA);
        Assert.Equal(2, version);

        var fresh = For(TenantA, out _);
        Assert.Equal("before", await fresh.UnprotectAsync("c", old));
        Assert.True(await fresh.NeedsReprotectAsync(old));
        Assert.StartsWith("v1.2.", (await fresh.ProtectAsync("c", "after")));

        var moved = (await fresh.ReprotectAsync("c", old))!;
        Assert.StartsWith("v1.2.", moved);
        Assert.Equal("before", await fresh.UnprotectAsync("c", moved));
        Assert.Equal(moved, await fresh.ReprotectAsync("c", moved));
        Assert.Equal(1, _store.Keys.Count(k => k.TenantId == TenantA && k.Purpose == KeyPurpose.Data && k.Status == KeyStatus.Active));
    }

    [Fact]
    public async Task Operations_need_a_tenant()
    {
        await using var scope = _root.CreateAsyncScope();
        var p = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        await Assert.ThrowsAsync<TenantRequiredException>(() => p.ProtectAsync("c", "x"));
    }

    [Fact]
    public async Task Stored_keys_are_wrapped_and_tenants_get_different_keys()
    {
        await For(TenantA, out _).ProtectAsync("c", "x");
        await For(TenantB, out _).ProtectAsync("c", "x");
        var keyA = _store.Keys.Single(k => k.TenantId == TenantA && k.Purpose == KeyPurpose.Data);
        var keyB = _store.Keys.Single(k => k.TenantId == TenantB && k.Purpose == KeyPurpose.Data);
        Assert.NotEqual(keyA.WrappedKey, keyB.WrappedKey);
        Assert.Equal(60, keyA.WrappedKey.Length); // 12 nonce + 16 tag + 32 key: wrapped, not the raw 32 bytes
        Assert.StartsWith("local:", keyA.KekId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_kek_rejects_the_wrong_key_id_and_tampered_wrapped_keys()
    {
        var kek = new LocalKeyEncryptionKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var wrapped = await kek.WrapAsync(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<EncryptionException>(() => kek.UnwrapAsync(wrapped, "local:other"));
        wrapped[^1] ^= 1;
        await Assert.ThrowsAsync<EncryptionException>(() => kek.UnwrapAsync(wrapped, kek.KeyId));
        Assert.Throws<EncryptionException>(() => new LocalKeyEncryptionKey(Convert.ToBase64String(new byte[16])));
    }

    [Theory]
    [InlineData("DE89370400440532013000", "••••3000")]
    [InlineData("SHORT", "••••")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Redactor_masks_all_but_the_tail(string? value, string expected) =>
        Assert.Equal(expected, AuditRedactor.MaskTail(value));

    private sealed class InMemoryKeyStore : ITenantKeyStore
    {
        private readonly List<StoredTenantKey> _keys = [];
        private readonly object _lock = new();

        public IReadOnlyList<StoredTenantKey> Keys
        {
            get { lock (_lock) { return _keys.ToList(); } }
        }

        public Task<IReadOnlyList<StoredTenantKey>> ListAsync(Guid tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoredTenantKey>>(Keys.Where(k => k.TenantId == tenantId).ToList());

        public Task<bool> TryAddAsync(StoredTenantKey key, int? retireVersion, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_keys.Any(k => k.TenantId == key.TenantId && k.Purpose == key.Purpose && k.Version == key.Version))
                {
                    return Task.FromResult(false);
                }

                if (retireVersion is { } v)
                {
                    var i = _keys.FindIndex(k => k.TenantId == key.TenantId && k.Purpose == key.Purpose && k.Version == v);
                    if (i >= 0)
                    {
                        _keys[i] = _keys[i] with { Status = KeyStatus.Retired };
                    }
                }

                _keys.Add(key);
                return Task.FromResult(true);
            }
        }
    }
}
