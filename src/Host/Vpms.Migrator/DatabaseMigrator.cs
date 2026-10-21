using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Audit.Infrastructure;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Identity.Infrastructure;

namespace Vpms.Migrator;

/// <summary>Applies every module's migrations. Runs with the migrator identity, never the app identity.</summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        var tenant = new TenantContext();
        using var _ = tenant.BeginPlatformScope();

        await using (var ctx = new CustomerAdminDbContext(Options<CustomerAdminDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new AuditDbContext(Options<AuditDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new IdentityDbContext(Options<IdentityDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }
    }

    private static DbContextOptions<T> Options<T>(string cs) where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseSqlServer(cs).Options;
}
