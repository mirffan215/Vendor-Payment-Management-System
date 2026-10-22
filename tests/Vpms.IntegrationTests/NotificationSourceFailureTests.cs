using Microsoft.Extensions.DependencyInjection;
using Vpms.Notifications.Application;
using Vpms.Notifications.Contracts;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class NotificationSourceFailureTests(SqlServerFixture sql) : IAsyncLifetime
{
    private WorkflowWorld _w = null!;
    private Guid _user;

    private sealed class Broken : INotificationSource
    {
        public string Name => "broken";

        public Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }

    private sealed class Working(Guid user) : INotificationSource
    {
        public string Name => "working";

        public Task<IReadOnlyList<NotificationEvent>> PollAsync(DateTimeOffset since, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationEvent>>([new NotificationEvent(NotificationKinds.PaymentReleased, "test:1", null, "PP-1", [user], null, "/payments", new Dictionary<string, string>(), DateTimeOffset.UtcNow)]);
    }

    public async Task InitializeAsync()
    {
        var db = await sql.CreateDatabaseAsync();
        // The users are created first; the sources are added when the host is built, so the user id is fixed up front.
        _user = Guid.NewGuid();
        _w = new WorkflowWorld(new IntegrationHost(db, null, services =>
        {
            services.AddScoped<INotificationSource, Broken>();
            services.AddScoped<INotificationSource>(_ => new Working(_user));
        }));
        await _w.InitializeAsync();
    }

    public async Task DisposeAsync() => await _w.Host.DisposeAsync();

    [Fact]
    public async Task A_failing_source_does_not_hold_back_the_others_and_is_reported()
    {
        var result = await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<INotificationSweep>().RunAsync());
        Assert.Contains("broken", result.FailedSources);
        Assert.Equal(1, result.Created);

        var again = await _w.AsAsync(_w.TenantA, _w.Admin, s => s.GetRequiredService<INotificationSweep>().RunAsync());
        Assert.Equal(0, again.Created);
        Assert.Contains("broken", again.FailedSources);
    }
}
