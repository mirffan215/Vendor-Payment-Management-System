using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
