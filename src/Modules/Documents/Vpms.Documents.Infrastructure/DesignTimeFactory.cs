using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Documents.Infrastructure;

internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DocumentsDbContext>
{
    public DocumentsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DocumentsDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
