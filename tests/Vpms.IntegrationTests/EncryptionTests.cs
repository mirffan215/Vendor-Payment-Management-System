using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class EncryptionTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public EncryptionTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync() => _host = new IntegrationHost(await _sql.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task First_use_creates_one_data_key_and_one_index_key_even_under_concurrency()
    {
        var ciphers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var scope = _host.Scope(_tenantA);
            var p = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
            return (Cipher: await p.ProtectAsync("c", "value-" + i), Index: await p.BlindIndexAsync("c", "value-" + i), I: i);
        }));

        var keys = await KeysAsync(_tenantA);
        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => k.Purpose == KeyPurpose.Data && k.Version == 1 && k.Status == KeyStatus.Active);
        Assert.Contains(keys, k => k.Purpose == KeyPurpose.Index && k.Version == 1);

        await using var read = _host.Scope(_tenantA);
        var reader = read.ServiceProvider.GetRequiredService<IFieldProtector>();
        foreach (var c in ciphers)
        {
            Assert.Equal("value-" + c.I, await reader.UnprotectAsync("c", c.Cipher));
        }
    }

    [Fact]
    public async Task Key_material_is_stored_wrapped_and_tenants_cannot_see_each_others_keys()
    {
        await Protect(_tenantA, "x");
        await Protect(_tenantB, "x");

        var a = await KeysAsync(_tenantA);
        var b = await KeysAsync(_tenantB);
        Assert.All(a, k => Assert.Equal(60, k.WrappedKey.Length));
        Assert.NotEqual(a.First(k => k.Purpose == KeyPurpose.Data).WrappedKey, b.First(k => k.Purpose == KeyPurpose.Data).WrappedKey);

        await using var scopeB = _host.Scope(_tenantB);
        var store = scopeB.ServiceProvider.GetRequiredService<ITenantKeyStore>();
        Assert.Empty(await store.ListAsync(_tenantA));

        // Even raw SQL under tenant B's session sees only tenant B's rows (row-level security).
        var db = scopeB.ServiceProvider.GetRequiredService<CustomerAdminDbContext>();
        var raw = await db.Database.SqlQueryRaw<Guid>("SELECT [TenantId] AS [Value] FROM [customer].[TenantKey]").ToListAsync();
        Assert.All(raw, t => Assert.Equal(_tenantB, t));
    }

    [Fact]
    public async Task Ciphertext_from_one_tenant_is_useless_to_another()
    {
        var cipher = await Protect(_tenantA, "300012345600003");
        await using var scope = _host.Scope(_tenantB);
        await Assert.ThrowsAsync<EncryptionException>(() =>
            scope.ServiceProvider.GetRequiredService<IFieldProtector>().UnprotectAsync("c", cipher));
    }

    [Fact]
    public async Task Rotation_is_audited_and_old_data_stays_readable()
    {
        var old = await Protect(_tenantA, "before");

        var manager = _host.Services.GetRequiredService<ITenantKeyManager>();
        Assert.Equal(2, await manager.RotateDataKeyAsync(_tenantA));

        await using (var scope = _host.Scope(_tenantA))
        {
            var p = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
            Assert.Equal("before", await p.UnprotectAsync("c", old));
            Assert.True(await p.NeedsReprotectAsync(old));
            Assert.StartsWith("v1.2.", await p.ProtectAsync("c", "after"));
        }

        var keys = await KeysAsync(_tenantA);
        Assert.Single(keys, k => k.Purpose == KeyPurpose.Data && k.Status == KeyStatus.Active && k.Version == 2);
        Assert.Single(keys, k => k.Purpose == KeyPurpose.Data && k.Status == KeyStatus.Retired && k.Version == 1);

        await using var audit = _host.Scope(_tenantA);
        var events = await audit.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Limit: 100), null);
        Assert.Contains(events.Items, e => e.Action == "encryption.key.created");
        Assert.Contains(events.Items, e => e.Action == "encryption.key.rotated" && e.EntityId == "data:2");
        Assert.DoesNotContain(events.Items, e => (e.ChangeSummaryJson ?? "").Contains("WrappedKey", StringComparison.OrdinalIgnoreCase));

        await using var verify = _host.Scope(null);
        Assert.True((await verify.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(_tenantA)).IsValid);
    }

    [Fact]
    public async Task Concurrent_rotations_do_not_create_two_active_keys()
    {
        await Protect(_tenantA, "x");
        var manager = _host.Services.GetRequiredService<ITenantKeyManager>();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            try { return await manager.RotateDataKeyAsync(_tenantA); }
            catch (EncryptionException) { return -1; }
        }));

        Assert.Contains(results, r => r > 1);
        var keys = await KeysAsync(_tenantA);
        Assert.Single(keys, k => k.Purpose == KeyPurpose.Data && k.Status == KeyStatus.Active);
    }

    private async Task<string> Protect(Guid tenant, string value)
    {
        await using var scope = _host.Scope(tenant);
        return (await scope.ServiceProvider.GetRequiredService<IFieldProtector>().ProtectAsync("c", value))!;
    }

    private async Task<List<StoredTenantKey>> KeysAsync(Guid tenant)
    {
        await using var scope = _host.Scope(tenant);
        return (await scope.ServiceProvider.GetRequiredService<ITenantKeyStore>().ListAsync(tenant)).ToList();
    }
}
