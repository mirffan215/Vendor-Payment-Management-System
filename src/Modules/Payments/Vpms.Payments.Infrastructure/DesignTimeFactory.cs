using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Payments.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
