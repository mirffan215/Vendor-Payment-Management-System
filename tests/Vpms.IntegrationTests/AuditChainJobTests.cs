using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vpms.BuildingBlocks.Audit;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.TestSupport;
using Vpms.Worker;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuditChainJobTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private IntegrationHost _host = null!;
    private TestDatabase _db = null!;
    private readonly Guid _healthy = Guid.NewGuid();
    private readonly Guid _tampered = Guid.NewGuid();

    public AuditChainJobTests(SqlServerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        _host = new IntegrationHost(_db);
        foreach (var tenant in new[] { _healthy, _tampered })
        {
            for (var i = 0; i < 3; i++)
            {
                await using var scope = _host.Scope(tenant);
                scope.ServiceProvider.GetRequiredService<IAuditPort>().Record(new AuditEntry("job.test." + i, "Thing"));
                await scope.ServiceProvider.GetRequiredService<CustomerAdminDbContext>().SaveChangesAsync();
            }
        }

        await using var conn = new SqlConnection(_db.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            EXEC sp_set_session_context N'IsPlatform', 1;
            DISABLE TRIGGER [audit].[trg_AuditEvent_Immutable] ON [audit].[AuditEvent];
            UPDATE [audit].[AuditEvent] SET [Action] = 'forged' WHERE [TenantId] = '{_tampered}' AND [Sequence] = 2;
            ENABLE TRIGGER [audit].[trg_AuditEvent_Immutable] ON [audit].[AuditEvent];
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Job_anchors_healthy_chains_and_raises_a_critical_alert_for_a_broken_one()
    {
        var logger = new ListLogger();
        var job = new AuditChainJob(_host.ScopeFactory, new ConfigurationBuilder().Build(), logger);

        await job.RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_host.AnchorDirectory, _healthy.ToString("N"))), "healthy tenant is anchored");
        Assert.False(Directory.Exists(Path.Combine(_host.AnchorDirectory, _tampered.ToString("N"))), "a broken chain is never anchored");

        var critical = Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        Assert.Contains(_tampered.ToString(), critical.Message, StringComparison.Ordinal);
        Assert.Contains("sequence 2", critical.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains(_healthy.ToString(), StringComparison.Ordinal));
    }

    private sealed class ListLogger : ILogger<AuditChainJob>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
