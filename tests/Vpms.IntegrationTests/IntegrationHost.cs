using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Security.Encryption;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Documents.Infrastructure;
using Vpms.VendorPortal.Application;
using Vpms.VendorPortal.Infrastructure;
using Vpms.Invoices.Infrastructure;
using Vpms.Payments.Infrastructure;
using Vpms.ErpIntegration.Infrastructure;
using Vpms.Reporting.Infrastructure;
using Vpms.Workflow.Infrastructure;
using Vpms.Vendors.Infrastructure;
using Vpms.Identity.Infrastructure;
using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

/// <summary>A DI container wired like the API, pointing at a test database through the restricted app login.</summary>
public sealed class IntegrationHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public TestDatabase Database { get; }
    public MockErpAdapter Erp { get; } = new();
    public string AnchorDirectory { get; } = Path.Combine(Path.GetTempPath(), "vpms-anchors-" + Guid.NewGuid().ToString("N"));

    public IntegrationHost(TestDatabase database, TimeProvider? clock = null, Action<IServiceCollection>? configure = null)
    {
        Database = database;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenancy();
        services.AddFieldEncryption();
        services.Configure<EncryptionOptions>(o => o.LocalKek = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        services.AddCustomerAdminModule(_ => database.AppConnectionString);
        services.AddIdentityModule(_ => database.AppConnectionString);
        services.AddAuditModule(_ => database.AppConnectionString);
        services.AddSingleton<IAuditAnchorSink>(new FileAuditAnchorSink(AnchorDirectory));
        services.Configure<StorageOptions>(o => o.Provider = "InMemory");
        services.AddBlobStorage();
        services.Configure<DocumentsOptions>(o => o.Scanner = "Eicar");
        services.AddDocumentsModule(_ => database.AppConnectionString);
        services.AddVendorsModule(_ => database.AppConnectionString);
        services.AddInvoicesModule(_ => database.AppConnectionString);
        services.AddWorkflowModule(_ => database.AppConnectionString);
        services.AddPaymentsModule(_ => database.AppConnectionString);
        services.AddSingleton<Vpms.BuildingBlocks.Security.ISecretReader>(new TestSecrets());
        services.AddErpIntegrationModule(_ => database.AppConnectionString);
        services.AddReportingModule();
        services.AddSingleton(Erp);
        services.AddSingleton<Vpms.ErpIntegration.Contracts.IErpAdapter>(Erp);
        services.Configure<PortalOptions>(o => o.PasswordHashIterations = 1000); // fast hashing for tests; production uses 600,000
        services.AddVendorPortalModule(_ => database.AppConnectionString);
        services.AddSingleton<Vpms.Identity.Application.ISecretResolver, NoSecrets>();
        // Registered last so it wins over the default clock the modules register.
        services.AddSingleton(clock ?? TimeProvider.System);
        configure?.Invoke(services);
        _root = services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>A new request/job scope bound to the tenant (or to no tenant when null).</summary>
    public IServiceProvider Services => _root;

    public AsyncServiceScope Scope(Guid? tenantId, Guid? actorId = null)
    {
        var scope = _root.CreateAsyncScope();
        if (actorId is { } actor)
        {
            var audit = scope.ServiceProvider.GetRequiredService<Vpms.BuildingBlocks.Audit.AuditContext>();
            audit.ActorId = actor;
            audit.ActorType = Vpms.BuildingBlocks.Audit.AuditActorType.User;
            audit.CorrelationId = "test-" + Guid.NewGuid().ToString("N")[..8];
        }

        if (tenantId is { } id)
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(id);
        }

        return scope;
    }

    public IServiceScopeFactory ScopeFactory => _root.GetRequiredService<IServiceScopeFactory>();

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}

/// <summary>No secret store in tests: every reference is "not available".</summary>
internal sealed class NoSecrets : Vpms.Identity.Application.ISecretResolver
{
    public Task<string?> ResolveAsync(string secretRef, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

/// <summary>Stands in for the vault in tests: every secret reference resolves to a fixed value.</summary>
public sealed class TestSecrets : Vpms.BuildingBlocks.Security.ISecretReader
{
    public const string Value = "test-secret-value";

    public Task<string?> ReadAsync(string secretRef, CancellationToken ct = default) => Task.FromResult<string?>(secretRef.StartsWith("missing", StringComparison.Ordinal) ? null : Value);
}
