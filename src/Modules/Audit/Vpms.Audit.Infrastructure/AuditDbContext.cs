using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Audit.Infrastructure;

public class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public const string Schema = "audit";

    public DbSet<AuditEvent> Events => Set<AuditEvent>();
    public DbSet<AuditChainHead> ChainHeads => Set<AuditChainHead>();
    public DbSet<AuditAnchor> Anchors => Set<AuditAnchor>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<AuditEvent>(b =>
        {
            b.ToTable("AuditEvent", t => t.HasTrigger("trg_AuditEvent_Immutable"));
            b.HasKey(x => x.Id);
            b.Property(x => x.OccurredAt).HasColumnType("datetimeoffset(7)");
            b.Property(x => x.ActorType).HasMaxLength(20).IsRequired();
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityId).HasMaxLength(100);
            b.Property(x => x.CorrelationId).HasMaxLength(100);
            b.Property(x => x.IpAddress).HasMaxLength(45);
            b.Property(x => x.BeforeHash).HasMaxLength(128);
            b.Property(x => x.AfterHash).HasMaxLength(128);
            b.Property(x => x.PrevHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Sequence }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId });
            b.HasIndex(x => new { x.TenantId, x.ActorId });
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasIndex(x => new { x.TenantId, x.CorrelationId });
        });

        modelBuilder.Entity<AuditChainHead>(b =>
        {
            b.ToTable("ChainHead");
            b.HasKey(x => x.TenantId);
            b.Property(x => x.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
        });

        modelBuilder.Entity<AuditAnchor>(b =>
        {
            b.ToTable("Anchor");
            b.HasKey(x => x.Id);
            b.Property(x => x.Hash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.ExternalRef).HasMaxLength(500).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Sequence });
        });

        base.OnModelCreating(modelBuilder);
    }
}
