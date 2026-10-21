using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.CustomerAdmin.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<CustomerAdminDbContext>
{
    public CustomerAdminDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CustomerAdminDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
