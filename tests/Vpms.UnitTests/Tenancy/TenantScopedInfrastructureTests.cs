using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Vpms.BuildingBlocks.Caching;
using Vpms.BuildingBlocks.Jobs;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.UnitTests.Tenancy;

public class TenantScopedInfrastructureTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static TenantContext For(Guid id)
    {
        var t = new TenantContext();
        t.SetTenant(id);
        return t;
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("c:evil")]
    public void Storage_path_rejects_unsafe_segments(string bad)
    {
        Assert.Throws<ArgumentException>(() => TenantStoragePath.Build(A, null, "invoice", Guid.NewGuid(), bad));
        Assert.Throws<ArgumentException>(() => TenantStoragePath.Build(A, null, bad, Guid.NewGuid(), "f.pdf"));
    }

    [Fact]
    public void Storage_path_is_prefixed_by_tenant_and_entity()
    {
        var entity = Guid.NewGuid();
        var key = TenantStoragePath.Build(A, entity, "invoice", Guid.NewGuid(), "f.pdf");
        Assert.StartsWith($"{A:N}/{entity:N}/invoice/", key);
        Assert.True(TenantStoragePath.BelongsTo(key, A));
        Assert.False(TenantStoragePath.BelongsTo(key, B));
    }

    [Theory]
    [InlineData("{A}/../{B}/x")]
    [InlineData("{A}\\x")]
    [InlineData("{B}/x")]
    [InlineData("")]
    public void Belongs_to_rejects_traversal_and_foreign_keys(string template)
    {
        var key = template.Replace("{A}", A.ToString("N")).Replace("{B}", B.ToString("N"));
        Assert.False(TenantStoragePath.BelongsTo(key, A));
    }

    [Fact]
    public async Task File_store_blocks_access_to_other_tenants_files()
    {
        var backend = new InMemoryBlobBackend();
        var storeA = new TenantScopedFileStore(backend, For(A));
        var storeB = new TenantScopedFileStore(backend, For(B));

        var key = await storeA.PutAsync(null, "doc", Guid.NewGuid(), "x.txt", new MemoryStream([1, 2, 3]));

        Assert.NotNull(await storeA.GetAsync(key));
        await Assert.ThrowsAsync<TenantViolationException>(() => storeB.GetAsync(key));
        await Assert.ThrowsAsync<TenantViolationException>(() => storeB.DeleteAsync(key));
        Assert.NotNull(await storeA.GetAsync(key));
    }

    [Fact]
    public async Task File_store_requires_a_tenant()
    {
        var store = new TenantScopedFileStore(new InMemoryBlobBackend(), new TenantContext());
        await Assert.ThrowsAsync<TenantRequiredException>(() =>
            store.PutAsync(null, "doc", Guid.NewGuid(), "x.txt", new MemoryStream()));
    }

    [Fact]
    public async Task Cache_entries_are_not_visible_across_tenants()
    {
        IDistributedCache shared = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cacheA = new TenantCache(shared, For(A));
        var cacheB = new TenantCache(shared, For(B));

        await cacheA.SetAsync("k", [9], TimeSpan.FromMinutes(1));

        Assert.NotNull(await cacheA.GetAsync("k"));
        Assert.Null(await cacheB.GetAsync("k"));
        await cacheB.RemoveAsync("k");
        Assert.NotNull(await cacheA.GetAsync("k"));
    }

    [Fact]
    public async Task Cache_requires_a_tenant()
    {
        IDistributedCache shared = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        await Assert.ThrowsAsync<TenantRequiredException>(() => new TenantCache(shared, new TenantContext()).GetAsync("k"));
    }

    [Fact]
    public async Task Job_without_tenant_is_dead_lettered_and_handler_not_run()
    {
        var ran = false;
        await Assert.ThrowsAsync<DeadLetterException>(() =>
            TenantJobRunner.RunAsync(new JobEnvelope<int>(Guid.Empty, "c", 1), new TenantContext(), _ => { ran = true; return Task.CompletedTask; }));
        await Assert.ThrowsAsync<DeadLetterException>(() =>
            TenantJobRunner.RunAsync<int>(null, new TenantContext(), _ => Task.CompletedTask));
        Assert.False(ran);
    }

    [Fact]
    public async Task Job_runs_inside_its_tenant_and_scope_cannot_switch_tenant()
    {
        var ctx = new TenantContext();
        Guid? seen = null;
        await TenantJobRunner.RunAsync(new JobEnvelope<int>(A, "c", 1), ctx, _ => { seen = ctx.TenantId; return Task.CompletedTask; });
        Assert.Equal(A, seen);
        Assert.Throws<TenantViolationException>(() => ctx.SetTenant(B));
    }

    [Fact]
    public void Platform_scope_is_temporary()
    {
        var ctx = new TenantContext();
        using (ctx.BeginPlatformScope())
        {
            Assert.True(ctx.IsPlatform);
        }

        Assert.False(ctx.IsPlatform);
    }
}
