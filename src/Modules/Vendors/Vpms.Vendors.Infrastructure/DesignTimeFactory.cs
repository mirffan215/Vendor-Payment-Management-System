using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Vendors.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<VendorsDbContext>
{
    public VendorsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VendorsDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
