using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Invoices.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<InvoicesDbContext>
{
    public InvoicesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InvoicesDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
