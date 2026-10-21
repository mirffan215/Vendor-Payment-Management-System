using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private TestDatabase _db = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private Guid _entityB;

    public TenantIsolationTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _entityB = Guid.NewGuid();

        var platform = new TenantContext();
        using var _ = platform.BeginPlatformScope();
        await using var ctx = Ctx(platform);
        ctx.Tenants.AddRange(
            new Tenant { Id = _tenantA, Name = "A", Slug = "a", CreatedAt = DateTimeOffset.UtcNow },
            new Tenant { Id = _tenantB, Name = "B", Slug = "b", CreatedAt = DateTimeOffset.UtcNow });
        ctx.LegalEntities.AddRange(
            Entity(Guid.NewGuid(), _tenantA, "A1"),
            Entity(Guid.NewGuid(), _tenantA, "A2"),
            Entity(_entityB, _tenantB, "B1"));
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Tenant_only_reads_its_own_rows()
    {
        await using var ctx = Ctx(For(_tenantA));
        var codes = await ctx.LegalEntities.Select(e => e.Code).OrderBy(c => c).ToListAsync();
        Assert.Equal(["A1", "A2"], codes);
        Assert.Equal(["A"], await ctx.Tenants.Select(t => t.Name).ToListAsync());
    }

    [Fact]
    public async Task No_tenant_means_no_rows()
    {
        await using var ctx = Ctx(new TenantContext());
        Assert.Empty(await ctx.LegalEntities.ToListAsync());
        Assert.Empty(await ctx.Tenants.ToListAsync());
    }

    [Fact]
    public async Task Cannot_read_another_tenants_row_by_id()
    {
        await using var ctx = Ctx(For(_tenantA));
        Assert.Null(await ctx.LegalEntities.FirstOrDefaultAsync(e => e.Id == _entityB));
    }

    [Fact]
    public async Task Cannot_write_a_row_for_another_tenant()
    {
        await using var ctx = Ctx(For(_tenantA));
        ctx.LegalEntities.Add(Entity(Guid.NewGuid(), _tenantB, "X"));
        await Assert.ThrowsAsync<TenantViolationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_id_is_stamped_from_context_when_not_set()
    {
        await using (var ctx = Ctx(For(_tenantA)))
        {
            var e = Entity(Guid.NewGuid(), Guid.Empty, "A3");
            ctx.LegalEntities.Add(e);
            await ctx.SaveChangesAsync();
            Assert.Equal(_tenantA, e.TenantId);
        }
    }

    [Fact]
    public async Task Cannot_update_or_delete_another_tenants_row_even_if_attached()
    {
        await using var ctx = Ctx(For(_tenantA));
        var foreign = Entity(_entityB, _tenantB, "B1");
        ctx.LegalEntities.Attach(foreign);
        foreign.LegalName = "hijacked";
        await Assert.ThrowsAsync<TenantViolationException>(() => ctx.SaveChangesAsync());

        ctx.ChangeTracker.Clear();
        ctx.LegalEntities.Remove(Entity(_entityB, _tenantB, "B1"));
        await Assert.ThrowsAsync<TenantViolationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenant_id_cannot_be_changed()
    {
        await using var ctx = Ctx(For(_tenantA));
        var mine = await ctx.LegalEntities.FirstAsync();
        mine.TenantId = _tenantB;
        await Assert.ThrowsAsync<TenantViolationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Row_level_security_blocks_reads_even_when_the_app_filter_is_bypassed()
    {
        // Raw SQL skips EF query filters entirely; only SQL Server RLS is left to protect the data.
        await using var ctx = Ctx(For(_tenantA));
        var tenants = await ctx.Database
            .SqlQueryRaw<Guid>("SELECT [TenantId] AS [Value] FROM [customer].[LegalEntity]").ToListAsync();
        Assert.NotEmpty(tenants);
        Assert.All(tenants, t => Assert.Equal(_tenantA, t));
    }

    [Fact]
    public async Task Row_level_security_blocks_writes_for_another_tenant()
    {
        await using var ctx = Ctx(For(_tenantA));
        var foreignTenant = _tenantB;
        var foreignEntity = _entityB;
        var ex = await Assert.ThrowsAsync<SqlException>(() => ctx.Database.ExecuteSqlAsync(
            $"INSERT INTO [customer].[LegalEntity] (Id, TenantId, Code, LegalName, CountryCode, BaseCurrency, Status, CreatedAt) VALUES (NEWID(), {foreignTenant}, 'EVIL', 'x', 'SA', 'SAR', 'Active', SYSDATETIMEOFFSET())"));
        Assert.Equal(33504, ex.Number); // block predicate violated

        var deleted = await ctx.Database.ExecuteSqlAsync($"DELETE FROM [customer].[LegalEntity] WHERE Id = {foreignEntity}");
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task Platform_scope_sees_all_tenants()
    {
        var platform = new TenantContext();
        using var _ = platform.BeginPlatformScope();
        await using var ctx = Ctx(platform);
        Assert.Equal(3, await ctx.LegalEntities.CountAsync());
        Assert.Equal(2, await ctx.Tenants.CountAsync());
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter()
    {
        using var ctx = Ctx(For(_tenantA));
        foreach (var et in ctx.Model.GetEntityTypes().Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType)))
        {
            Assert.True(et.GetDeclaredQueryFilters().Count > 0, $"{et.ClrType.Name} has no tenant query filter");
        }
    }

    private static TenantContext For(Guid tenantId)
    {
        var t = new TenantContext();
        t.SetTenant(tenantId);
        return t;
    }

    private CustomerAdminDbContext Ctx(TenantContext tenant) =>
        new(new DbContextOptionsBuilder<CustomerAdminDbContext>()
            .UseSqlServer(_db.AppConnectionString)
            .AddInterceptors(new TenantSessionContextInterceptor(tenant))
            .Options, tenant);

    private static LegalEntity Entity(Guid id, Guid tenantId, string code) => new()
    {
        Id = id, TenantId = tenantId, Code = code, LegalName = code, CountryCode = "SA", BaseCurrency = "SAR",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
