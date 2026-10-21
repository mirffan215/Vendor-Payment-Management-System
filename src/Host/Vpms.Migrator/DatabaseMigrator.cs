using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Infrastructure;

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
    }

    private static DbContextOptions<T> Options<T>(string cs) where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseSqlServer(cs).Options;
}
