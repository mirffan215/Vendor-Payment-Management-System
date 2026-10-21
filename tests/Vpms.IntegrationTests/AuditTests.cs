using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.Audit.Domain;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuditTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public AuditTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    // --- append, atomicity, chain --------------------------------------------------------------------------

    [Fact]
    public async Task Business_change_and_audit_record_are_committed_together()
    {
        await using (var scope = _host.Scope(_tenantA))
        {
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<CustomerAdminDbContext>().LegalEntities.Add(Entity("A1"));
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("legal-entity.created", "LegalEntity", "A1"));
            await sp.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
        }

        var events = await EventsAsync(_tenantA);
        var e = Assert.Single(events);
        Assert.Equal(1, e.Sequence);
        Assert.Equal("legal-entity.created", e.Action);
        Assert.Equal(AuditHasher.GenesisHash, e.PrevHash);
        Assert.Equal(64, e.Hash.Length);
        Assert.Equal(1, await CountEntitiesAsync(_tenantA));
        Assert.True((await VerifyAsync(_tenantA)).IsValid);
    }

    [Fact]
    public async Task Failed_business_change_leaves_no_audit_record_and_no_sequence_gap()
    {
        await using (var scope = _host.Scope(_tenantA))
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<CustomerAdminDbContext>();
            db.LegalEntities.Add(Entity("DUP"));
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("legal-entity.created", "LegalEntity", "DUP"));
            await db.SaveChangesAsync();
        }

        await using (var scope = _host.Scope(_tenantA))
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<CustomerAdminDbContext>();
            db.LegalEntities.Add(Entity("DUP")); // violates the unique (TenantId, Code) index
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("legal-entity.created", "LegalEntity", "DUP-again"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using (var scope = _host.Scope(_tenantA))
        {
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<CustomerAdminDbContext>().LegalEntities.Add(Entity("OK"));
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("legal-entity.created", "LegalEntity", "OK"));
            await sp.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
        }

        var events = await EventsAsync(_tenantA);
        Assert.Equal([1L, 2L], events.Select(e => e.Sequence));
        Assert.DoesNotContain(events, e => e.EntityId == "DUP-again");
        Assert.True((await VerifyAsync(_tenantA)).IsValid);
    }

    [Fact]
    public async Task Concurrent_writers_produce_a_gap_free_valid_chain()
    {
        const int writers = 20;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            await using var scope = _host.Scope(_tenantA);
            var sp = scope.ServiceProvider;
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("test.concurrent", "Thing", i.ToString()));
            sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("test.concurrent.second", "Thing", i.ToString()));
            sp.GetRequiredService<CustomerAdminDbContext>().LegalEntities.Add(Entity("C" + i));
            await sp.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
        }));

        var events = await EventsAsync(_tenantA);
        Assert.Equal(Enumerable.Range(1, writers * 2).Select(i => (long)i), events.Select(e => e.Sequence));
        var result = await VerifyAsync(_tenantA);
        Assert.True(result.IsValid, result.Problem);
        Assert.Equal(writers * 2, result.EventsChecked);
    }

    [Fact]
    public async Task Each_tenant_has_its_own_chain()
    {
        await Write(_tenantA, "a.one");
        await Write(_tenantB, "b.one");
        await Write(_tenantA, "a.two");

        Assert.Equal([1L, 2L], (await EventsAsync(_tenantA)).Select(e => e.Sequence));
        Assert.Equal([1L], (await EventsAsync(_tenantB)).Select(e => e.Sequence));
        Assert.Equal(AuditHasher.GenesisHash, (await EventsAsync(_tenantB))[0].PrevHash);
    }

    [Fact]
    public async Task Events_can_be_written_without_a_tenant_context_when_the_entry_names_the_tenant()
    {
        await using (var scope = _host.Scope(null))
        {
            await scope.ServiceProvider.GetRequiredService<IAuditPort>().WriteNowAsync(
                new AuditEntry("auth.login.denied", "User", TenantId: _tenantA, ActorType: AuditActorType.Anonymous));
        }

        var e = Assert.Single(await EventsAsync(_tenantA));
        Assert.Equal(AuditActorType.Anonymous, e.ActorType);
    }

    // --- immutability --------------------------------------------------------------------------------------

    [Fact]
    public async Task Application_login_cannot_update_or_delete_audit_events()
    {
        await Write(_tenantA, "x");

        await using var conn = new SqlConnection(_db.AppConnectionString);
        await conn.OpenAsync();
        await SetSession(conn, _tenantA);

        var update = await Assert.ThrowsAsync<SqlException>(() => Exec(conn, "UPDATE [audit].[AuditEvent] SET [Action] = 'tampered'"));
        Assert.Equal(229, update.Number); // permission denied
        var delete = await Assert.ThrowsAsync<SqlException>(() => Exec(conn, "DELETE FROM [audit].[AuditEvent]"));
        Assert.Equal(229, delete.Number);
        await Assert.ThrowsAsync<SqlException>(() => Exec(conn, "TRUNCATE TABLE [audit].[AuditEvent]"));

        Assert.Single(await EventsAsync(_tenantA));
    }

    [Fact]
    public async Task Even_the_database_owner_is_stopped_by_the_trigger()
    {
        await Write(_tenantA, "x");

        await using var conn = new SqlConnection(_db.OwnerConnectionString);
        await conn.OpenAsync();
        // Row-level security applies to the owner too; switch to the platform context so the rows are really targeted.
        await Exec(conn, "EXEC sp_set_session_context N'IsPlatform', 1");
        var update = await Assert.ThrowsAsync<SqlException>(() => Exec(conn, "UPDATE [audit].[AuditEvent] SET [Action] = 'tampered'"));
        Assert.Equal(51000, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => Exec(conn, "DELETE FROM [audit].[AuditEvent]"));
        Assert.Equal(51000, delete.Number);

        var events = await EventsAsync(_tenantA);
        Assert.Single(events);
        Assert.Equal("x", events[0].Action);
    }

    // --- tamper evidence (an attacker who defeats the controls above) --------------------------------------

    [Fact]
    public async Task Modified_event_is_detected()
    {
        await WriteMany(_tenantA, 3);
        await PrivilegedAsync("UPDATE [audit].[AuditEvent] SET [Action] = 'forged' WHERE [Sequence] = 2");

        var result = await VerifyAsync(_tenantA);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public async Task Deleted_event_in_the_middle_is_detected()
    {
        await WriteMany(_tenantA, 3);
        await PrivilegedAsync("DELETE FROM [audit].[AuditEvent] WHERE [Sequence] = 2");

        var result = await VerifyAsync(_tenantA);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public async Task Removed_tail_events_are_detected_through_the_chain_head()
    {
        await WriteMany(_tenantA, 3);
        await PrivilegedAsync("DELETE FROM [audit].[AuditEvent] WHERE [Sequence] = 3");

        var result = await VerifyAsync(_tenantA);
        Assert.False(result.IsValid);
        Assert.Contains("head", result.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fully_recomputed_chain_is_still_caught_by_an_external_anchor()
    {
        await WriteMany(_tenantA, 4);
        await using (var scope = _host.Scope(null))
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAuditAnchorService>().AnchorAsync(_tenantA));
        }

        // The attacker rewrites event 2 and recomputes every later hash and the head, so the chain is internally consistent.
        await RewriteChainAsync(_tenantA, rewriteSequence: 2);

        var result = await VerifyAsync(_tenantA);
        Assert.False(result.IsValid);
        Assert.Contains("anchored", result.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.GetFiles(Path.Combine(_host.AnchorDirectory, _tenantA.ToString("N"))).Length == 1);
    }

    [Fact]
    public async Task Anchoring_is_idempotent_until_new_events_arrive()
    {
        await WriteMany(_tenantA, 2);
        await using var scope = _host.Scope(null);
        var anchors = scope.ServiceProvider.GetRequiredService<IAuditAnchorService>();
        Assert.True(await anchors.AnchorAsync(_tenantA));
        Assert.False(await anchors.AnchorAsync(_tenantA));
        Assert.False(await anchors.AnchorAsync(Guid.NewGuid()), "no events, nothing to anchor");
    }

    // --- tenant isolation of the audit trail ---------------------------------------------------------------

    [Fact]
    public async Task Tenants_cannot_read_each_others_audit_events_even_with_raw_sql()
    {
        await Write(_tenantA, "a.secret");
        await Write(_tenantB, "b.secret");

        await using var scope = _host.Scope(_tenantB);
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.Equal(["b.secret"], await db.Events.Select(e => e.Action).ToListAsync());
        var raw = await db.Database.SqlQueryRaw<string>("SELECT [Action] AS [Value] FROM [audit].[AuditEvent]").ToListAsync();
        Assert.Equal(["b.secret"], raw);
    }

    [Fact]
    public async Task Audit_entry_for_another_tenant_cannot_be_buffered_into_this_scope()
    {
        await using var scope = _host.Scope(_tenantA);
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IAuditPort>().Record(new AuditEntry("evil", "X", TenantId: _tenantB));
        sp.GetRequiredService<CustomerAdminDbContext>().LegalEntities.Add(Entity("Z"));
        await Assert.ThrowsAsync<TenantViolationException>(() => sp.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync());
        Assert.Empty(await EventsAsync(_tenantB));
    }

    [Fact]
    public async Task Query_service_filters_by_entity_scope_and_pages_by_sequence()
    {
        var entity1 = Guid.NewGuid();
        var entity2 = Guid.NewGuid();
        await using (var scope = _host.Scope(_tenantA))
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditPort>();
            audit.Record(new AuditEntry("e1.a", "Thing", LegalEntityId: entity1));
            audit.Record(new AuditEntry("e2.a", "Thing", LegalEntityId: entity2));
            audit.Record(new AuditEntry("tenant.level", "Thing"));
            audit.Record(new AuditEntry("e1.b", "Thing", LegalEntityId: entity1));
            scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().LegalEntities.Add(Entity("Q"));
            await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
        }

        await using var read = _host.Scope(_tenantA);
        var query = read.ServiceProvider.GetRequiredService<IAuditQueryService>();

        var scoped = await query.QueryAsync(new AuditQuery(), new HashSet<Guid> { entity1 });
        Assert.Equal(["e1.a", "e1.b"], scoped.Items.Select(e => e.Action));

        var all = await query.QueryAsync(new AuditQuery(), null);
        Assert.Equal(4, all.Items.Count);

        var page1 = await query.QueryAsync(new AuditQuery(Limit: 3), null);
        Assert.Equal(3, page1.Items.Count);
        Assert.NotNull(page1.NextAfterSequence);
        var page2 = await query.QueryAsync(new AuditQuery(AfterSequence: page1.NextAfterSequence, Limit: 3), null);
        Assert.Equal(["e1.b"], page2.Items.Select(e => e.Action));
        Assert.Null(page2.NextAfterSequence);

        var denied = await query.QueryAsync(new AuditQuery(LegalEntityId: entity2), new HashSet<Guid> { entity1 });
        Assert.Empty(denied.Items);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private static LegalEntity Entity(string code) => new()
    {
        Id = Guid.NewGuid(), Code = code, LegalName = code, CountryCode = "SA", BaseCurrency = "SAR",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task Write(Guid tenantId, string action)
    {
        await using var scope = _host.Scope(tenantId);
        scope.ServiceProvider.GetRequiredService<IAuditPort>().Record(new AuditEntry(action, "Thing", "1"));
        await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
    }

    private async Task WriteMany(Guid tenantId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Write(tenantId, "event." + i);
        }
    }

    private async Task<List<AuditEvent>> EventsAsync(Guid tenantId)
    {
        await using var scope = _host.Scope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.AsNoTracking()
            .OrderBy(e => e.Sequence).ToListAsync();
    }

    private async Task<int> CountEntitiesAsync(Guid tenantId)
    {
        await using var scope = _host.Scope(tenantId);
        return await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().LegalEntities.CountAsync();
    }

    private async Task<ChainVerificationResult> VerifyAsync(Guid tenantId)
    {
        await using var scope = _host.Scope(null);
        return await scope.ServiceProvider.GetRequiredService<IAuditChainVerifier>().VerifyAsync(tenantId);
    }

    /// <summary>Simulates an attacker with owner rights who disables the trigger first.</summary>
    private async Task PrivilegedAsync(string sql)
    {
        await using var conn = new SqlConnection(_db.OwnerConnectionString);
        await conn.OpenAsync();
        await Exec(conn, "EXEC sp_set_session_context N'IsPlatform', 1");
        await Exec(conn, "DISABLE TRIGGER [audit].[trg_AuditEvent_Immutable] ON [audit].[AuditEvent]");
        try
        {
            await Exec(conn, sql);
        }
        finally
        {
            await Exec(conn, "ENABLE TRIGGER [audit].[trg_AuditEvent_Immutable] ON [audit].[AuditEvent]");
        }
    }

    private async Task RewriteChainAsync(Guid tenantId, long rewriteSequence)
    {
        var events = await EventsAsync(tenantId);
        var prev = AuditHasher.GenesisHash;
        foreach (var e in events)
        {
            if (e.Sequence == rewriteSequence)
            {
                e.Action = "forged";
            }

            e.PrevHash = prev;
            e.Hash = AuditHasher.Compute(prev, e);
            prev = e.Hash;
            await PrivilegedAsync(
                $"UPDATE [audit].[AuditEvent] SET [Action] = '{e.Action}', [PrevHash] = '{e.PrevHash}', [Hash] = '{e.Hash}' WHERE [Sequence] = {e.Sequence}");
        }

        await PrivilegedAsync($"UPDATE [audit].[ChainHead] SET [Hash] = '{prev}' WHERE [TenantId] = '{tenantId}'");
    }

    private static async Task SetSession(SqlConnection conn, Guid tenantId) =>
        await Exec(conn, $"EXEC sp_set_session_context N'TenantId', '{tenantId}'");

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
