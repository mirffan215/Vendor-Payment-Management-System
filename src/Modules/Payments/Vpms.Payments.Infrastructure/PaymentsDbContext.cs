using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Payments.Domain;

namespace Vpms.Payments.Infrastructure;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "payment";

    public DbSet<PaymentRequest> Requests => Set<PaymentRequest>();
    public DbSet<PaymentProposal> Proposals => Set<PaymentProposal>();
    public DbSet<PaymentProposalItem> Items => Set<PaymentProposalItem>();
    public DbSet<ReleaseAuthorization> Authorizations => Set<ReleaseAuthorization>();
    public DbSet<PaymentInstruction> Instructions => Set<PaymentInstruction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<PaymentRequest>(b =>
        {
            b.ToTable("PaymentRequest");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.InvoiceNumber).HasMaxLength(100).IsRequired();
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.InvoiceHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.BankFingerprint).HasMaxLength(200).IsRequired();
            b.Property(x => x.InvolvedList).IsRequired();
            b.Ignore(x => x.Involved);
            b.Property(x => x.Status).HasMaxLength(12).IsRequired();
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status, x.DueDate });
            b.HasIndex(x => new { x.TenantId, x.ProposalId });
            // an invoice has at most one request that still claims it
            b.HasIndex(x => x.InvoiceId).IsUnique().HasFilter("[Status] IN ('Created', 'InProposal', 'Released')").HasDatabaseName("UX_PaymentRequest_OpenInvoice");
        });

        modelBuilder.Entity<PaymentProposal>(b =>
        {
            b.ToTable("PaymentProposal");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ProposalNo).HasMaxLength(30).IsRequired();
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.TotalAmount).HasPrecision(19, 4);
            b.Property(x => x.Status).HasMaxLength(15).IsRequired();
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.ApprovalComment).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.SequenceNo }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status });
        });

        modelBuilder.Entity<PaymentProposalItem>(b =>
        {
            b.ToTable("PaymentProposalItem");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Amount).HasPrecision(19, 4);
            b.HasIndex(x => new { x.ProposalId, x.PaymentRequestId }).IsUnique();
            b.HasIndex(x => x.PaymentRequestId);
        });

        modelBuilder.Entity<ReleaseAuthorization>(b =>
        {
            b.ToTable("ReleaseAuthorization");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Decision).HasMaxLength(10).IsRequired();
            b.Property(x => x.DenialReason).HasMaxLength(500);
            b.Property(x => x.RecheckJson).IsRequired();
            b.Property(x => x.SodJson).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ProposalId });
        });

        modelBuilder.Entity<PaymentInstruction>(b =>
        {
            b.ToTable("PaymentInstruction");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Format).HasMaxLength(30).IsRequired();
            b.Property(x => x.CsvCipher).IsRequired();
            b.Property(x => x.JsonCipher).IsRequired();
            b.Property(x => x.Hash).HasMaxLength(64).IsRequired();
            b.Property(x => x.Signature).HasMaxLength(64).IsRequired();
            b.Property(x => x.TotalAmount).HasPrecision(19, 4);
            b.Property(x => x.CurrencyCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Status).HasMaxLength(10).IsRequired();
            b.HasIndex(x => x.ProposalId).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
