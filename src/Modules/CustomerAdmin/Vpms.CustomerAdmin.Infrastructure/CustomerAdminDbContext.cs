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
    public DbSet<TenantKey> TenantKeys => Set<TenantKey>();
    public DbSet<ApprovalAuthority> ApprovalAuthorities => Set<ApprovalAuthority>();
    public DbSet<ApprovalMatrixRule> ApprovalMatrixRules => Set<ApprovalMatrixRule>();
    public DbSet<RetentionPolicyVersion> RetentionPolicies => Set<RetentionPolicyVersion>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();

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
            b.Property(x => x.DefaultCalendar).HasMaxLength(10).IsRequired();
            b.Property(x => x.PrimaryColor).HasMaxLength(7);
            // The tenant row itself is visible only to its own tenant.
            b.HasQueryFilter(x => x.Id == CurrentTenantId || IsPlatformScope);
        });

        modelBuilder.Entity<LegalEntity>(b =>
        {
            b.ToTable("LegalEntity");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(50).IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.LegalNameAr).HasMaxLength(300);
            b.Property(x => x.TaxRegistrationNoCipher).HasMaxLength(500);
            b.Property(x => x.TaxRegistrationNoIndex).HasMaxLength(64).IsFixedLength();
            b.HasIndex(x => new { x.TenantId, x.TaxRegistrationNoIndex }).IsUnique().HasFilter("[TaxRegistrationNoIndex] IS NOT NULL");
            b.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
            b.Property(x => x.BaseCurrency).HasMaxLength(3).IsRequired();
            b.Property(x => x.Status).HasMaxLength(30).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<ApprovalAuthority>(b =>
        {
            b.ToTable("ApprovalAuthority", t => t.HasCheckConstraint("CK_ApprovalAuthority_Amount", "[MaxAmount] > 0"));
            b.HasKey(x => x.Id);
            b.Property(x => x.MaxAmount).HasPrecision(19, 4);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.UserId });
        });

        modelBuilder.Entity<ApprovalMatrixRule>(b =>
        {
            b.ToTable("ApprovalMatrixRule");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Priority });
        });

        modelBuilder.Entity<TenantKey>(b =>
        {
            b.ToTable("TenantKey");
            b.HasKey(x => x.Id);
            b.Property(x => x.Purpose).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.KekId).HasMaxLength(500).IsRequired();
            b.Property(x => x.WrappedKey).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Purpose, x.Version }).IsUnique();
            // At most one active key per tenant and purpose.
            b.HasIndex(x => new { x.TenantId, x.Purpose }).IsUnique().HasFilter("[Status] = 'Active'")
                .HasDatabaseName("UX_TenantKey_Active");
        });

        modelBuilder.Entity<RetentionPolicyVersion>(b =>
        {
            b.ToTable("RetentionPolicyVersion");
            b.HasKey(x => x.Id);
            b.Property(x => x.SettingsJson).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500).IsRequired();
            b.Property(x => x.DecisionComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.VersionNo }).IsUnique();
            // One active and one pending version at a time.
            b.HasIndex(x => x.TenantId).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_RetentionPolicy_Active");
            b.HasIndex(x => x.TenantId).IsUnique().HasFilter("[Status] = 'Pending'").HasDatabaseName("UX_RetentionPolicy_Pending");
        });

        modelBuilder.Entity<LegalHold>(b =>
        {
            b.ToTable("LegalHold");
            b.HasKey(x => x.Id);
            b.Property(x => x.Scope).HasMaxLength(20).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            b.Property(x => x.ReleaseComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.Ignore(x => x.IsActive);
            b.HasIndex(x => new { x.TenantId, x.ReleasedAt });
        });

        base.OnModelCreating(modelBuilder);
    }
}
