using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vpms.Audit.Application;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Audit;
using Vpms.BuildingBlocks.Cloud;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Infrastructure;

namespace Vpms.Migrator;

/// <summary>
/// A person's decision after the database was restored to an earlier state: the tenant's audit chain is sound on its own, and the events after the backup are
/// accepted as lost. Publishes a restore marker outside the database and records the decision in the chain.
/// </summary>
public static class RestoreAcceptance
{
    public static async Task<ChainVerificationResult> AcceptAsync(string cs, IConfiguration configuration, string slug, string reason, CancellationToken ct = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddTenancy();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.AddAnchorBackend(AuditModule.AnchorBackendKey);
        services.AddAuditModule(_ => cs);
        services.AddAuditAnchorSink();
        await using var provider = services.BuildServiceProvider();

        Guid tenantId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
            using var platform = tenant.BeginPlatformScope();
            var options = new DbContextOptionsBuilder<CustomerAdminDbContext>().UseSqlServer(cs)
                .AddInterceptors(scope.ServiceProvider.GetRequiredService<TenantSessionContextInterceptor>()).Options;
            await using var customer = new CustomerAdminDbContext(options, tenant);
            tenantId = await customer.Tenants.AsNoTracking().Where(t => t.Slug == slug).Select(t => t.Id).FirstOrDefaultAsync(ct);
            if (tenantId == Guid.Empty) throw new InvalidOperationException($"No tenant with slug '{slug}'.");
        }

        await using var work = provider.CreateAsyncScope();
        work.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        var context = work.ServiceProvider.GetRequiredService<AuditContext>();
        context.ActorType = AuditActorType.System;
        context.CorrelationId = "accept-restore";
        var result = await work.ServiceProvider.GetRequiredService<IAuditRestoreService>().AcceptRestoreAsync(tenantId, reason, ct);
        if (!result.IsValid) throw new InvalidOperationException($"The restored audit chain is not sound: {result.Problem}");
        return result;
    }
}
