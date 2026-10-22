using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Vpms.Audit.Application;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Storage;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Contracts;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Migrator;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>A customer leaving: suspend, resume, and the final shred of keys and files (ADR-0024).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class TenantOffboardingTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private IntegrationHost _host = null!;
    private WorkflowWorld _w = null!;
    private string Owner => _host.Database.OwnerConnectionString;

    public async Task InitializeAsync()
    {
        _host = new IntegrationHost(await sql.CreateDatabaseAsync(), _clock);
        _w = new WorkflowWorld(_host);
        await _w.InitializeAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> SlugAsync(Guid tenant)
    {
        await using var scope = _host.Scope(null);
        return (await scope.ServiceProvider.GetRequiredService<ITenantLookup>().FindSlugAsync(tenant))!;
    }

    private async Task<bool> ActiveAsync(Guid tenant)
    {
        await using var scope = _host.Scope(null);
        return await scope.ServiceProvider.GetRequiredService<ITenantLookup>().IsActiveAsync(tenant);
    }

    private async Task<string> StatusAsync(Guid tenant)
    {
        await using var scope = _host.Scope(null);
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().BeginPlatformScope();
        return (await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant)).Status;
    }

    private async Task<List<(string Purpose, string Status, int Bytes)>> KeysAsync(Guid tenant)
    {
        await using var scope = _host.Scope(tenant);
        return (await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().TenantKeys.AsNoTracking().ToListAsync()).Select(k => (k.Purpose, k.Status, k.WrappedKey.Length)).ToList();
    }

    private async Task<string> ProtectAsync(Guid tenant, string value)
    {
        await using var scope = _host.Scope(tenant);
        return (await scope.ServiceProvider.GetRequiredService<IFieldProtector>().ProtectAsync("test.column", value))!;
    }

    private async Task<string?> UnprotectAsync(Guid tenant, string cipher)
    {
        await using var scope = _host.Scope(tenant);
        return await scope.ServiceProvider.GetRequiredService<IFieldProtector>().UnprotectAsync("test.column", cipher);
    }

    private async Task<string> PutFileAsync(Guid tenant)
    {
        var key = TenantStoragePath.Build(tenant, null, "attachments", Guid.NewGuid(), "doc.pdf");
        await _host.Services.GetRequiredService<IBlobBackend>().PutAsync(key, new MemoryStream([1, 2, 3]));
        return key;
    }

    private async Task<bool> FileExistsAsync(string key)
    {
        await using var stream = await _host.Services.GetRequiredService<IBlobBackend>().GetAsync(key);
        return stream is not null;
    }

    private Task<ShredResult> ShredAsync(Guid tenant, string? confirm = null, int minDays = 30) =>
        TenantOffboarding.ShredAsync(Owner, SlugAsync(tenant).Result, confirm ?? SlugAsync(tenant).Result, minDays, _host.Services.GetRequiredService<IBlobBackend>(), _clock);

    [Fact]
    public async Task Suspending_stops_a_tenant_and_resuming_brings_it_back()
    {
        var slug = await SlugAsync(_w.TenantA);
        Assert.True(await ActiveAsync(_w.TenantA));
        await TenantOffboarding.SuspendAsync(Owner, slug, "Customer asked to pause");
        Assert.False(await ActiveAsync(_w.TenantA));
        Assert.Equal(TenantStatus.Suspended, await StatusAsync(_w.TenantA));
        await using (var scope = _host.Scope(null))
        {
            var lookup = scope.ServiceProvider.GetRequiredService<ITenantLookup>();
            Assert.DoesNotContain(await lookup.ListActiveAsync(), t => t.TenantId == _w.TenantA);
            Assert.Null(await lookup.FindTenantIdBySlugAsync(slug));
            Assert.Contains(await lookup.ListActiveAsync(), t => t.TenantId == _w.TenantB);
        }

        await TenantOffboarding.ResumeAsync(Owner, slug, "Back again");
        Assert.True(await ActiveAsync(_w.TenantA));

        var events = await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Action: "tenant.suspended"), null));
        Assert.Single(events.Items);
        Assert.Single((await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Action: "tenant.resumed"), null))).Items);
    }

    [Fact]
    public async Task A_status_change_needs_a_reason_and_the_right_starting_status()
    {
        var slug = await SlugAsync(_w.TenantA);
        await Assert.ThrowsAsync<ArgumentException>(() => TenantOffboarding.SuspendAsync(Owner, slug, " "));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TenantOffboarding.ResumeAsync(Owner, slug, "not suspended"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TenantOffboarding.SuspendAsync(Owner, "no-such-tenant", "x"));
    }

    [Fact]
    public async Task Shredding_is_refused_until_every_safeguard_is_met()
    {
        var slug = await SlugAsync(_w.TenantA);
        await ProtectAsync(_w.TenantA, "secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShredAsync(_w.TenantA)); // still active
        await TenantOffboarding.SuspendAsync(Owner, slug, "Leaving");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShredAsync(_w.TenantA)); // cooling-off not over
        _clock.Advance(TimeSpan.FromDays(31));
        await Assert.ThrowsAsync<ArgumentException>(() => ShredAsync(_w.TenantA, confirm: "wrong-slug"));

        // an active legal hold blocks it
        await using (var scope = _host.Scope(_w.TenantA, _w.Admin))
        {
            var holds = scope.ServiceProvider.GetRequiredService<Vpms.CustomerAdmin.Application.ILegalHoldService>();
            await holds.PlaceAsync(HoldScope.Tenant, null, null, "Litigation");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShredAsync(_w.TenantA));
        Assert.All(await KeysAsync(_w.TenantA), k => Assert.NotEqual(KeyStatus.Destroyed, k.Status));
        Assert.Equal(TenantStatus.Suspended, await StatusAsync(_w.TenantA));
    }

    [Fact]
    public async Task Shredding_destroys_the_keys_deletes_the_files_and_leaves_other_tenants_and_the_audit_trail_alone()
    {
        var slug = await SlugAsync(_w.TenantA);
        var cipherA = await ProtectAsync(_w.TenantA, "Account SA0380000000608010167519");
        var cipherB = await ProtectAsync(_w.TenantB, "Other tenant secret");
        var filesA = new[] { await PutFileAsync(_w.TenantA), await PutFileAsync(_w.TenantA) };
        var fileB = await PutFileAsync(_w.TenantB);
        Assert.Equal("Account SA0380000000608010167519", await UnprotectAsync(_w.TenantA, cipherA));
        var keysBefore = await KeysAsync(_w.TenantA);

        await TenantOffboarding.SuspendAsync(Owner, slug, "Contract ended");
        _clock.Advance(TimeSpan.FromDays(31));
        var result = await ShredAsync(_w.TenantA);

        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(keysBefore.Count, result.KeysDestroyed);
        Assert.Equal(TenantStatus.Offboarded, await StatusAsync(_w.TenantA));

        // the keys are gone for good: tombstones remain, no new key appears, nothing can be read or written
        var keysAfter = await KeysAsync(_w.TenantA);
        Assert.Equal(keysBefore.Count, keysAfter.Count);
        Assert.All(keysAfter, k => { Assert.Equal(KeyStatus.Destroyed, k.Status); Assert.Equal(0, k.Bytes); });
        _clock.Advance(TimeSpan.FromMinutes(11)); // past the in-memory key cache
        await Assert.ThrowsAsync<TenantKeyDestroyedException>(() => UnprotectAsync(_w.TenantA, cipherA));
        await Assert.ThrowsAsync<TenantKeyDestroyedException>(() => ProtectAsync(_w.TenantA, "new data"));
        Assert.Equal(keysBefore.Count, (await KeysAsync(_w.TenantA)).Count);

        foreach (var f in filesA) Assert.False(await FileExistsAsync(f));
        Assert.True(await FileExistsAsync(fileB));
        Assert.Equal("Other tenant secret", await UnprotectAsync(_w.TenantB, cipherB));
        Assert.True(await ActiveAsync(_w.TenantB));
        Assert.False(await ActiveAsync(_w.TenantA));

        // the audit trail of the departed tenant is intact and records what happened
        await using var scope = _host.Scope(_w.TenantA, _w.Admin);
        var verified = await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(_w.TenantA);
        Assert.True(verified.IsValid, verified.Problem);
        var query = scope.ServiceProvider.GetRequiredService<IAuditQueryService>();
        Assert.Single((await query.QueryAsync(new AuditQuery(Action: "tenant.shred.started"), null)).Items);
        var done = Assert.Single((await query.QueryAsync(new AuditQuery(Action: "tenant.offboarded"), null)).Items);
        Assert.Contains("\"filesDeleted\":2", done.ChangeSummaryJson);
    }

    [Fact]
    public async Task A_shredded_tenant_cannot_be_resumed_or_shredded_again()
    {
        var slug = await SlugAsync(_w.TenantA);
        await TenantOffboarding.SuspendAsync(Owner, slug, "Leaving");
        _clock.Advance(TimeSpan.FromDays(31));
        await ShredAsync(_w.TenantA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TenantOffboarding.ResumeAsync(Owner, slug, "undo"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShredAsync(_w.TenantA));
    }
}
