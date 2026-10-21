using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>A DI container wired like the API, pointing at a test database through the restricted app login.</summary>
public sealed class IntegrationHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public TestDatabase Database { get; }
    public string AnchorDirectory { get; } = Path.Combine(Path.GetTempPath(), "vpms-anchors-" + Guid.NewGuid().ToString("N"));

    public IntegrationHost(TestDatabase database, TimeProvider? clock = null)
    {
        Database = database;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddTenancy();
        services.AddCustomerAdminModule(_ => database.AppConnectionString);
        services.AddIdentityModule(_ => database.AppConnectionString);
        services.AddAuditModule(_ => database.AppConnectionString);
        services.AddSingleton<IAuditAnchorSink>(new FileAuditAnchorSink(AnchorDirectory));
        _root = services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>A new request/job scope bound to the tenant (or to no tenant when null).</summary>
    public AsyncServiceScope Scope(Guid? tenantId)
    {
        var scope = _root.CreateAsyncScope();
        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(id);
        }

        return scope;
    }

    public IServiceScopeFactory ScopeFactory => _root.GetRequiredService<IServiceScopeFactory>();

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}
