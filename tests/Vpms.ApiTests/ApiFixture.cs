using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Vpms.Api.Auth;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Application;
using Vpms.Identity.Application;
using Vpms.Identity.Domain;
using Vpms.Identity.Infrastructure;
using Serilog.Core;
using Serilog.Events;
using Vpms.TestSupport;

namespace Vpms.ApiTests;

/// <summary>Boots the real API against a migrated SQL Server and two fake identity providers (one per tenant).</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly SqlServerFixture _sql;
    private TestDatabase _db = null!;

    public ApiFixture(SqlServerFixture sql) => _sql = sql;

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public CollectingLogSink LogSink { get; } = new();
    public FakeOidcProvider IdpA { get; private set; } = null!;
    public FakeOidcProvider IdpB { get; private set; } = null!;
    public Guid TenantA { get; } = Guid.NewGuid();
    public Guid TenantB { get; } = Guid.NewGuid();
    public Guid ProviderA { get; } = Guid.NewGuid();
    public Guid ProviderB { get; } = Guid.NewGuid();
    public Guid DisabledProvider { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _db = await _sql.CreateDatabaseAsync();
        IdpA = await FakeOidcProvider.StartAsync("a", "client-a", "secret-a");
        IdpB = await FakeOidcProvider.StartAsync("b", "client-b", "secret-b");
        await SeedAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = _db.AppConnectionString,
            ["Secrets:secret-a"] = "secret-a",
            ["Secrets:secret-b"] = "secret-b",
            ["Documents:Scanner"] = "Eicar",
            ["Portal:PasswordHashIterations"] = "1000",
            ["RateLimiting:PortalAuthPermitLimit"] = "10000", // tests share one client address; dedicated tests lower it
            ["RateLimiting:AuthPermitLimit"] = "10000",
            ["Portal:PublicBaseUrl"] = "https://portal.test",
            ["Encryption:LocalKek"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(LogSink);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<OidcBackchannelOptions>();
            services.AddSingleton(new OidcBackchannelOptions { Handler = new RoutingHandler(IdpA, IdpB) });
        });
    }

    /// <summary>Sends back-channel OIDC calls to whichever fake provider owns the path.</summary>
    private sealed class RoutingHandler(FakeOidcProvider a, FakeOidcProvider b) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var inner = request.RequestUri!.AbsolutePath.StartsWith("/idp/b", StringComparison.Ordinal) ? b : a;
            var invoker = new HttpMessageInvoker(inner.Handler, disposeHandler: false);
            return invoker.SendAsync(request, cancellationToken);
        }
    }

    public HttpClient NewBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost"),
    });

    public async Task<T> WithDbAsync<T>(Func<IdentityDbContext, CustomerAdminDbContext, Task<T>> action)
    {
        var tenant = new TenantContext();
        using var _ = tenant.BeginPlatformScope();
        await using var identity = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseSqlServer(_db.AppConnectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenant)).Options, tenant);
        await using var admin = new CustomerAdminDbContext(
            new DbContextOptionsBuilder<CustomerAdminDbContext>().UseSqlServer(_db.AppConnectionString)
                .AddInterceptors(new TenantSessionContextInterceptor(tenant)).Options, tenant);
        return await action(identity, admin);
    }

    /// <summary>Gives an existing user a role (system roles are created on demand), as a tenant administrator would.</summary>
    public async Task AssignRoleAsync(Guid tenantId, string email, string roleName, Guid? legalEntityId)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var roles = scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>();
        await roles.EnsureSystemRolesAsync();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email);
        var role = await db.Roles.FirstAsync(r => r.Name == roleName);
        await roles.AssignAsync(user.Id, user.Id, role.Id, legalEntityId);
    }

    /// <summary>Creates a custom role with the given permissions and assigns it (tenant-wide or to one entity).</summary>
    public async Task CreateRoleAndAssign(Guid tenantId, string email, string roleName, string[] permissions, Guid? legalEntityId)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role is null)
        {
            role = new Role { Id = Guid.NewGuid(), Name = roleName, Permissions = permissions.Select(p => new RolePermission { Id = Guid.NewGuid(), Permission = p }).ToList() };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        var user = await db.Users.FirstAsync(u => u.Email == email);
        await scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>().AssignAsync(user.Id, user.Id, role.Id, legalEntityId);
    }

    /// <summary>Runs the virus scan the worker would run, for every attachment waiting in quarantine.</summary>
    public async Task ScanAllAsync()
    {
        IReadOnlyList<PendingScan> pending;
        await using (var list = Services.CreateAsyncScope())
        {
            pending = await list.ServiceProvider.GetRequiredService<IAttachmentScanService>().ListPendingAsync(200);
        }

        foreach (var item in pending)
        {
            await using var scope = Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(item.TenantId);
            await scope.ServiceProvider.GetRequiredService<IAttachmentScanService>().ScanAsync(item.AttachmentId);
        }
    }

    private async Task SeedAsync() => await WithDbAsync<int>(async (identity, admin) =>
    {
        admin.Tenants.AddRange(
            new Tenant { Id = TenantA, Name = "Acme", Slug = "acme", CreatedAt = DateTimeOffset.UtcNow },
            new Tenant { Id = TenantB, Name = "Beta", Slug = "beta", CreatedAt = DateTimeOffset.UtcNow });
        await admin.SaveChangesAsync();

        identity.IdentityProviders.AddRange(
            Provider(ProviderA, TenantA, IdpA, "secret-a", "acme.com", enabled: true),
            Provider(ProviderB, TenantB, IdpB, "secret-b", "beta.com", enabled: true),
            Provider(DisabledProvider, TenantA, IdpA, "secret-a", "acme.com", enabled: false));
        await identity.SaveChangesAsync();
        return 0;
    });

    private static IdentityProviderConfig Provider(Guid id, Guid tenantId, FakeOidcProvider idp, string secretRef,
        string domains, bool enabled) => new()
    {
        Id = id, TenantId = tenantId, Kind = IdentityProviderKind.GenericOidc, DisplayName = "Corporate SSO " + idp.Name,
        Authority = idp.Authority, ClientId = idp.ClientId, ClientSecretRef = secretRef,
        Enabled = enabled, AutoProvision = true, AllowedEmailDomains = domains,
    };

    public new async Task DisposeAsync()
    {
        await IdpA.DisposeAsync();
        await IdpB.DisposeAsync();
        await base.DisposeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync();
}

public sealed class CollectingLogSink : ILogEventSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
