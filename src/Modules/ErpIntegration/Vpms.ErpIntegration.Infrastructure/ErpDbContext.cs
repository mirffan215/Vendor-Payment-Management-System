using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Infrastructure;

public class ErpDbContext(DbContextOptions<ErpDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "erp";

    public DbSet<ErpConfig> Configs => Set<ErpConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ErpConfig>(b =>
        {
            b.ToTable("ErpConfig");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(12).IsRequired();
            b.Property(x => x.ConfigJson).IsRequired();
            b.Property(x => x.Status).HasMaxLength(12).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500).IsRequired();
            b.Property(x => x.DecisionComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.Kind, x.LegalEntityId, x.VersionNo }).IsUnique().HasFilter(null);
            b.HasIndex(x => new { x.TenantId, x.Kind, x.LegalEntityId }).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_ErpConfig_Active");
            b.HasIndex(x => new { x.TenantId, x.Kind, x.LegalEntityId }).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("UX_ErpConfig_Pending");
        });
    }
}

internal sealed class DesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ErpDbContext>
{
    public ErpDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=.;Database=VpmsDesignTime;Trusted_Connection=True;TrustServerCertificate=True").Options,
            new TenantContext());
}
