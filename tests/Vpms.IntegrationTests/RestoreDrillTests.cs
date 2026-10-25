using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Storage;
using Vpms.Migrator;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>
/// The restore drill (docs/operations/restore.md) as a test: back the database up, carry on working, restore the backup into a new database, and check that
/// the checks an operator runs afterwards notice the lost events, that a person's acceptance lets the chain continue, and that encrypted data still reads.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RestoreDrillTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly string _kek = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private readonly InMemoryBlobBackend _anchorStore = new();
    private IntegrationHost _source = null!;
    private WorkflowWorld _w = null!;
    private IntegrationHost? _restored;

    private void Configure(IServiceCollection services)
    {
        services.RemoveAll<IAuditAnchorSink>();
        services.AddSingleton<IAuditAnchorSink>(new BlobAuditAnchorSink(_anchorStore)); // outside the database, like the immutable container
        services.Configure<EncryptionOptions>(o => o.LocalKek = _kek);                  // the same key-encryption key, as in a real restore
    }

    public async Task InitializeAsync()
    {
        _source = new IntegrationHost(await sql.CreateDatabaseAsync(), configure: Configure);
        _w = new WorkflowWorld(_source);
        await _w.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        if (_restored is not null) await _restored.DisposeAsync();
    }

    private async Task WorkAsync(IntegrationHost host, string prefix, int count)
    {
        for (var i = 0; i < count; i++) await ActiveVendorKit.CreateEntityAsync(host.Services, _w.TenantA, _w.Admin, $"{prefix}{i}", "SA", "SAR");
    }

    private static async Task<bool> AnchorAsync(IntegrationHost host, Guid tenant)
    {
        await using var scope = host.Scope(null);
        return await scope.ServiceProvider.GetRequiredService<IAuditAnchorService>().AnchorAsync(tenant);
    }

    private static async Task<ChainVerificationResult> VerifyAsync(IntegrationHost host, Guid tenant)
    {
        await using var scope = host.Scope(null);
        return await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(tenant);
    }

    private static async Task Exec(string connectionString, string statement)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = statement;
        cmd.CommandTimeout = 120;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Backs the database up on the SQL Server, and restores that backup under a new name, as an operator would on a new server.</summary>
    private async Task<(string BackupFile, Func<Task<TestDatabase>> Restore)> BackupAsync()
    {
        var owner = new SqlConnectionStringBuilder(_source.Database.OwnerConnectionString);
        var name = owner.InitialCatalog;
        owner.InitialCatalog = "master";
        var file = $"/var/opt/mssql/data/{name}.bak";
        await Exec(owner.ConnectionString, $"BACKUP DATABASE [{name}] TO DISK = N'{file}' WITH INIT, CHECKSUM");

        async Task<TestDatabase> Restore()
        {
            var target = "vpms_restored_" + Guid.NewGuid().ToString("N");
            var logical = new List<(string Name, string Type)>();
            await using (var conn = new SqlConnection(owner.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{file}'";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) logical.Add((reader.GetString(0), reader.GetString(2)));
            }

            var moves = string.Join(", ", logical.Select(l => $"MOVE N'{l.Name}' TO N'/var/opt/mssql/data/{target}{(l.Type == "L" ? "_log.ldf" : ".mdf")}'"));
            await Exec(owner.ConnectionString, $"RESTORE DATABASE [{target}] FROM DISK = N'{file}' WITH CHECKSUM, {moves}");
            var restoredOwner = new SqlConnectionStringBuilder(owner.ConnectionString) { InitialCatalog = target };
            var app = new SqlConnectionStringBuilder(_source.Database.AppConnectionString) { InitialCatalog = target };
            return new TestDatabase(restoredOwner.ConnectionString, app.ConnectionString);
        }

        return (file, Restore);
    }

    [Fact]
    public async Task A_restore_to_an_earlier_point_is_noticed_accepted_by_a_person_and_the_trail_carries_on()
    {
        await WorkAsync(_source, "B", 3);
        Assert.True(await AnchorAsync(_source, _w.TenantA));
        string cipher;
        await using (var scope = _source.Scope(_w.TenantA))
        {
            cipher = (await scope.ServiceProvider.GetRequiredService<IFieldProtector>().ProtectAsync("test.column", "IBAN SA0380000000608010167519"))!;
        }

        var (_, restore) = await BackupAsync();

        // work goes on after the backup, and is anchored outside the database
        await WorkAsync(_source, "C", 4);
        Assert.True(await AnchorAsync(_source, _w.TenantA));
        var live = await VerifyAsync(_source, _w.TenantA);
        Assert.True(live.IsValid, live.Problem);

        // disaster: the database is brought back from the backup
        _restored = new IntegrationHost(await restore(), configure: Configure);
        var after = await VerifyAsync(_restored, _w.TenantA);
        Assert.False(after.IsValid);
        Assert.Contains("Events are missing", after.Problem);
        Assert.True(after.EventsChecked < live.EventsChecked);

        // the operator's check says the same, and says nothing else is wrong: files, keys and the rest are whole
        var findings = await RestoreVerification.RunAsync(_restored.Services, _restored.Database.OwnerConnectionString);
        var finding = Assert.Single(findings);
        Assert.Equal((_w.TenantA, "audit-chain"), (finding.TenantId, finding.Check));

        // encrypted data from before the backup still reads with the same key-encryption key
        await using (var scope = _restored.Scope(_w.TenantA))
        {
            Assert.Equal("IBAN SA0380000000608010167519", await scope.ServiceProvider.GetRequiredService<IFieldProtector>().UnprotectAsync("test.column", cipher));
        }

        // a person accepts the restore: the chain must be sound on its own, then it continues
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var scope = _restored.Scope(_w.TenantA, _w.Admin);
            await scope.ServiceProvider.GetRequiredService<IAuditRestoreService>().AcceptRestoreAsync(_w.TenantA, " ");
        });
        await using (var scope = _restored.Scope(_w.TenantA, _w.Admin))
        {
            var accepted = await scope.ServiceProvider.GetRequiredService<IAuditRestoreService>().AcceptRestoreAsync(_w.TenantA, "Restored after storage failure, ticket 4411");
            Assert.True(accepted.IsValid, accepted.Problem);
        }

        Assert.Empty(await RestoreVerification.RunAsync(_restored.Services, _restored.Database.OwnerConnectionString));
        await WorkAsync(_restored, "D", 2);
        Assert.True(await AnchorAsync(_restored, _w.TenantA));
        var continued = await VerifyAsync(_restored, _w.TenantA);
        Assert.True(continued.IsValid, continued.Problem);

        // the decision is on the record, inside the chain and outside the database
        var external = await new BlobAuditAnchorSink(_anchorStore).ListAsync(_w.TenantA);
        Assert.Contains(external, a => a.Kind == ExternalAnchorKind.Restore && a.Reason!.Contains("4411", StringComparison.Ordinal));
        await using (var scope = _restored.Scope(_w.TenantA))
        {
            var events = await scope.ServiceProvider.GetRequiredService<IAuditQueryService>().QueryAsync(new AuditQuery(Action: "audit.restore.accepted"), null);
            Assert.Single(events.Items);
        }
    }

    [Fact]
    public async Task A_restore_that_loses_nothing_passes_every_check_without_anyone_accepting_anything()
    {
        await WorkAsync(_source, "B", 3);
        Assert.True(await AnchorAsync(_source, _w.TenantA));
        var (_, restore) = await BackupAsync();

        _restored = new IntegrationHost(await restore(), configure: Configure);
        var verified = await VerifyAsync(_restored, _w.TenantA);
        Assert.True(verified.IsValid, verified.Problem);
        Assert.Empty(await RestoreVerification.RunAsync(_restored.Services, _restored.Database.OwnerConnectionString));
    }

    [Fact]
    public async Task Anchors_in_the_container_are_write_once_listed_in_order_and_a_restore_marker_retires_the_ones_before_it()
    {
        var sink = new BlobAuditAnchorSink(_anchorStore);
        var tenant = Guid.NewGuid();
        await sink.PublishAsync(tenant, 5, "aaaaaaaaaaaaaaaaaaaa");
        await sink.PublishAsync(tenant, 9, "bbbbbbbbbbbbbbbbbbbb");
        await sink.PublishRestoreMarkerAsync(tenant, 5, "restored");
        await sink.PublishAsync(tenant, 7, "cccccccccccccccccccc");

        var listed = await sink.ListAsync(tenant);
        Assert.Equal([ExternalAnchorKind.Anchor, ExternalAnchorKind.Anchor, ExternalAnchorKind.Restore, ExternalAnchorKind.Anchor], listed.Select(a => a.Kind));
        Assert.Equal([5L, 9L, 5L, 7L], listed.Select(a => a.Sequence));
        Assert.Empty(await sink.ListAsync(Guid.NewGuid())); // another tenant's anchors are not visible
        var keys = new List<string>();
        await foreach (var k in _anchorStore.ListKeysAsync(TenantStoragePath.PrefixFor(tenant))) keys.Add(k);
        Assert.Equal(4, keys.Count);
        await Assert.ThrowsAnyAsync<Exception>(() => _anchorStore.PutAsync(keys[0], new MemoryStream([1])));
    }
}
