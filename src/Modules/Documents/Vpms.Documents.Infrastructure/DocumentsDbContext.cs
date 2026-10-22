using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Documents.Domain;

namespace Vpms.Documents.Infrastructure;

public class DocumentsDbContext(DbContextOptions<DocumentsDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public const string Schema = "documents";

    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("Attachment");
            b.HasKey(x => x.Id);
            b.Property(x => x.OwnerType).HasMaxLength(50).IsRequired();
            b.Property(x => x.OwnerId).HasMaxLength(100).IsRequired();
            b.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            b.Property(x => x.ScanStatus).HasMaxLength(20).IsRequired();
            b.Property(x => x.ScanSignature).HasMaxLength(200);
            b.Property(x => x.UploaderKind).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.OwnerType, x.OwnerId });
            b.HasIndex(x => x.ScanStatus);
        });

        base.OnModelCreating(modelBuilder);
    }
}
