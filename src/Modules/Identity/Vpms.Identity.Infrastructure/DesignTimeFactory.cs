using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Identity.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
