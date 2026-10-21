using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Vendors.Domain;

namespace Vpms.Vendors.Infrastructure;

public class VendorsDbContext(DbContextOptions<VendorsDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public const string Schema = "vendor";

    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorDocument> Documents => Set<VendorDocument>();
    public DbSet<VendorEntityLink> EntityLinks => Set<VendorEntityLink>();
    public DbSet<VendorRequest> Requests => Set<VendorRequest>();
    public DbSet<VendorMessage> Messages => Set<VendorMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Vendor>(b =>
        {
            b.ToTable("Vendor");
            b.HasKey(x => x.Id);
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.LegalNameAr).HasMaxLength(300);
            b.Property(x => x.TradeName).HasMaxLength(300);
            b.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength().IsRequired();
            b.Property(x => x.TaxIdCipher).HasMaxLength(500);
            b.Property(x => x.TaxIdIndex).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.RegistrationNoCipher).HasMaxLength(500);
            b.Property(x => x.ContactEmail).HasMaxLength(320);
            b.Property(x => x.ContactPhone).HasMaxLength(50);
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.VerificationStatus).HasMaxLength(20).IsRequired();
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.SubmittedByKind).HasMaxLength(10);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.TaxIdIndex });
            b.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<VendorDocument>(b =>
        {
            b.ToTable("VendorDocument");
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).HasMaxLength(40).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.ReviewNote).HasMaxLength(500);
            b.Property(x => x.UploaderKind).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.VendorId });
            b.HasIndex(x => x.AttachmentId).IsUnique();
        });

        modelBuilder.Entity<VendorEntityLink>(b =>
        {
            b.ToTable("VendorEntityLink");
            b.HasKey(x => x.Id);
            b.Property(x => x.ErpVendorAccount).HasMaxLength(50);
            b.Property(x => x.PaymentTermsCode).HasMaxLength(30);
            b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.DefaultTaxCode).HasMaxLength(30);
            b.Property(x => x.WithholdingRuleCode).HasMaxLength(30);
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.VendorId, x.LegalEntityId }).IsUnique();
        });

        modelBuilder.Entity<VendorRequest>(b =>
        {
            b.ToTable("VendorRequest");
            b.HasKey(x => x.Id);
            b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.VendorId });
        });

        modelBuilder.Entity<VendorMessage>(b =>
        {
            b.ToTable("VendorMessage");
            b.HasKey(x => x.Id);
            b.Property(x => x.Direction).HasMaxLength(20).IsRequired();
            b.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            b.Property(x => x.AuthorKind).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.RequestId });
        });

        base.OnModelCreating(modelBuilder);
    }
}
