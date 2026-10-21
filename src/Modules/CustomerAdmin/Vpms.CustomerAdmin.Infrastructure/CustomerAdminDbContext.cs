using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Domain;

namespace Vpms.CustomerAdmin.Infrastructure;

public class CustomerAdminDbContext(DbContextOptions<CustomerAdminDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public const string Schema = "customer";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<LegalEntity> LegalEntities => Set<LegalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenant");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            b.Property(x => x.DefaultLocale).HasMaxLength(10).IsRequired();
            // The tenant row itself is visible only to its own tenant.
            b.HasQueryFilter(x => x.Id == CurrentTenantId || IsPlatformScope);
        });

        modelBuilder.Entity<LegalEntity>(b =>
        {
            b.ToTable("LegalEntity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(50).IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
            b.Property(x => x.BaseCurrency).HasMaxLength(3).IsRequired();
            b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
