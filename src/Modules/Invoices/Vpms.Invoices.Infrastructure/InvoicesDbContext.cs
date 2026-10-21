using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Invoices.Domain;

namespace Vpms.Invoices.Infrastructure;

public class InvoicesDbContext(DbContextOptions<InvoicesDbContext> options, ITenantContext tenant, VendorContext? vendor = null)
    : TenantDbContext(options, tenant, vendor)
{
    public const string Schema = "invoice";

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> Lines => Set<InvoiceLine>();
    public DbSet<InvoiceAttachment> Attachments => Set<InvoiceAttachment>();
    public DbSet<InvoiceExtraction> Extractions => Set<InvoiceExtraction>();
    public DbSet<InvoiceMailbox> Mailboxes => Set<InvoiceMailbox>();
    public DbSet<IntakeMessage> IntakeMessages => Set<IntakeMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("Invoice");
            b.HasKey(x => x.Id);
            b.Property(x => x.InvoiceNumber).HasMaxLength(100).IsRequired();
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Subtotal).HasPrecision(19, 4);
            b.Property(x => x.TaxTotal).HasPrecision(19, 4);
            b.Property(x => x.Total).HasPrecision(19, 4);
            b.Property(x => x.PoReference).HasMaxLength(100);
            b.Property(x => x.SupplierTaxInvoiceRef).HasMaxLength(200);
            b.Property(x => x.Source).HasMaxLength(20).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.DuplicateKeyIndex).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.NumberKeyIndex).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.CreatedByKind).HasMaxLength(10).IsRequired();
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.VendorId, x.NumberKeyIndex });
            b.HasIndex(x => new { x.TenantId, x.DuplicateKeyIndex });
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("InvoiceLine");
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(19, 6);
            b.Property(x => x.UnitPrice).HasPrecision(19, 6);
            b.Property(x => x.LineAmount).HasPrecision(19, 4);
            b.Property(x => x.TaxCode).HasMaxLength(30);
            b.Property(x => x.TaxRate).HasPrecision(9, 6);
            b.Property(x => x.TaxAmount).HasPrecision(19, 4);
            b.Property(x => x.PoLineRef).HasMaxLength(50);
            b.HasIndex(x => new { x.InvoiceId, x.LineNo }).IsUnique();
        });

        modelBuilder.Entity<InvoiceAttachment>(b =>
        {
            b.ToTable("InvoiceAttachment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Role).HasMaxLength(20).IsRequired();
            b.Property(x => x.AddedByKind).HasMaxLength(10).IsRequired();
            b.HasIndex(x => x.AttachmentId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.InvoiceId });
        });

        modelBuilder.Entity<InvoiceExtraction>(b =>
        {
            b.ToTable("InvoiceExtraction");
            b.HasKey(x => x.Id);
            b.Property(x => x.Engine).HasMaxLength(50).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.ResultJson).IsRequired();
            b.HasIndex(x => x.AttachmentId).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.InvoiceId });
        });

        modelBuilder.Entity<InvoiceMailbox>(b =>
        {
            b.ToTable("InvoiceMailbox");
            b.HasKey(x => x.Id);
            b.Property(x => x.Address).HasMaxLength(320).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Address }).IsUnique();
        });

        modelBuilder.Entity<IntakeMessage>(b =>
        {
            b.ToTable("IntakeMessage");
            b.HasKey(x => x.Id);
            b.Property(x => x.ExternalMessageId).HasMaxLength(300).IsRequired();
            b.Property(x => x.FromAddress).HasMaxLength(320).IsRequired();
            b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(500);
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.AttachmentIds).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.ExternalMessageId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status });
        });

        base.OnModelCreating(modelBuilder);
    }
}
