using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.ErpIntegration.Domain;

namespace Vpms.ErpIntegration.Infrastructure;

public class ErpDbContext(DbContextOptions<ErpDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "erp";

    public DbSet<ErpConfig> Configs => Set<ErpConfig>();
    public DbSet<ErpSyncRecord> SyncRecords => Set<ErpSyncRecord>();
    public DbSet<ErpSyncAttempt> SyncAttempts => Set<ErpSyncAttempt>();
    public DbSet<ErpInboxMessage> Inbox => Set<ErpInboxMessage>();
    public DbSet<ErpPullState> PullStates => Set<ErpPullState>();
    public DbSet<ReconciliationRun> ReconRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationItem> ReconItems => Set<ReconciliationItem>();

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

        modelBuilder.Entity<ErpSyncRecord>(b =>
        {
            b.ToTable("ErpSyncRecord");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(20).IsRequired();
            b.Property(x => x.ObjectVersion).HasMaxLength(64).IsRequired();
            b.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            b.Property(x => x.Status).HasMaxLength(12).IsRequired();
            b.Property(x => x.LeaseOwner).HasMaxLength(100);
            b.Property(x => x.LastErrorCode).HasMaxLength(60);
            b.Property(x => x.LastError).HasMaxLength(500);
            b.Property(x => x.ErpReference).HasMaxLength(100);
            b.Property(x => x.HandlingNote).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            // the same business event can never be queued twice
            b.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptAt });
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Type, x.ObjectId });
        });

        modelBuilder.Entity<ErpInboxMessage>(b =>
        {
            b.ToTable("ErpInboxMessage");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            b.Property(x => x.ExternalId).HasMaxLength(100).IsRequired();
            b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.MessageId).HasMaxLength(250).IsRequired();
            b.Property(x => x.PayloadJson).IsRequired();
            b.Property(x => x.Status).HasMaxLength(10).IsRequired();
            b.Property(x => x.Error).HasMaxLength(500);
            // a message that says the same thing is stored once, however often it arrives
            b.HasIndex(x => new { x.TenantId, x.MessageId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Kind, x.ExternalId, x.Sequence });
            b.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<ErpPullState>(b =>
        {
            b.ToTable("ErpPullState");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            b.Property(x => x.LastError).HasMaxLength(200);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<ReconciliationRun>(b =>
        {
            b.ToTable("ReconciliationRun");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasMaxLength(10).IsRequired();
            b.Property(x => x.ErrorCode).HasMaxLength(60);
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.StartedAt });
        });

        modelBuilder.Entity<ReconciliationItem>(b =>
        {
            b.ToTable("ReconciliationItem");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(24).IsRequired();
            b.Property(x => x.ItemKey).HasMaxLength(200).IsRequired();
            b.Property(x => x.InvoiceNumber).HasMaxLength(100);
            b.Property(x => x.ErpReference).HasMaxLength(100);
            b.Property(x => x.VpmsValue).HasMaxLength(300).IsRequired();
            b.Property(x => x.ErpValue).HasMaxLength(300).IsRequired();
            b.Property(x => x.Status).HasMaxLength(18).IsRequired();
            b.Property(x => x.ProposedReason).HasMaxLength(500);
            b.Property(x => x.DecisionReason).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsRowVersion();
            // the same difference found again is the same item
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.ItemKey }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LegalEntityId, x.Status });
        });

        modelBuilder.Entity<ErpSyncAttempt>(b =>
        {
            b.ToTable("ErpSyncAttempt");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
            b.Property(x => x.Code).HasMaxLength(60);
            b.Property(x => x.Message).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.RecordId, x.AttemptNo });
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
