using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Notifications.Domain;
using Vpms.Notifications.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>Indexes and statements added after the Phase 8 load tests (docs/performance.md) stay in place.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PerformanceIndexTests(SqlServerFixture sql) : IAsyncLifetime
{
    private IntegrationHost _host = null!;

    public async Task InitializeAsync() => _host = new IntegrationHost(await sql.CreateDatabaseAsync());

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<bool> IndexExistsAsync(string table, string name)
    {
        await using var conn = new SqlConnection(_host.Database.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = @n AND object_id = OBJECT_ID(@t)";
        cmd.Parameters.AddWithValue("@n", name);
        cmd.Parameters.AddWithValue("@t", table);
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }

    [Fact]
    public async Task The_invoice_list_sorts_from_an_index_and_the_notification_sweep_can_find_known_events()
    {
        Assert.True(await IndexExistsAsync("invoice.Invoice", "IX_Invoice_TenantId_CreatedAt_Id"));
        Assert.True(await IndexExistsAsync("notify.Notification", "IX_Notification_TenantId_DedupeKey"));
    }

    [Fact]
    public async Task Marking_everything_read_is_one_statement_that_touches_only_my_unread_notifications()
    {
        var tenant = Guid.NewGuid();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        await using (var scope = _host.Scope(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            for (var i = 0; i < 5; i++) db.Notifications.Add(new Notification { Id = Guid.NewGuid(), UserId = i < 4 ? me : other, Kind = "ApprovalAssigned", SubjectLabel = "x", Path = "/x", DedupeKey = "k" + i, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using (var scope = _host.Scope(tenant, me))
        {
            Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<Vpms.Notifications.Application.INotificationService>().MarkAllReadAsync());
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Vpms.Notifications.Application.INotificationService>().MarkAllReadAsync());
        }

        await using (var scope = _host.Scope(tenant, other))
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Vpms.Notifications.Application.INotificationService>().UnreadCountAsync());
        }
    }
}
