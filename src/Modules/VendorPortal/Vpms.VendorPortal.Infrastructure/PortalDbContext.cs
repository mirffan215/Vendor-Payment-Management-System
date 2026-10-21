using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.VendorPortal.Domain;

namespace Vpms.VendorPortal.Infrastructure;

public class PortalDbContext(DbContextOptions<PortalDbContext> options, ITenantContext tenant) : TenantDbContext(options, tenant)
{
    public const string Schema = "portal";

    public DbSet<VendorUser> Users => Set<VendorUser>();
    public DbSet<VendorInvite> Invites => Set<VendorInvite>();
    public DbSet<VendorRecoveryCode> RecoveryCodes => Set<VendorRecoveryCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<VendorUser>(b =>
        {
            b.ToTable("VendorUser");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.Property(x => x.TotpSecretCipher).HasMaxLength(500);
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.VendorId });
        });

        modelBuilder.Entity<VendorInvite>(b =>
        {
            b.ToTable("VendorInvite");
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.Purpose).HasMaxLength(20).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.VendorUserId });
        });

        modelBuilder.Entity<VendorRecoveryCode>(b =>
        {
            b.ToTable("VendorRecoveryCode");
            b.HasKey(x => x.Id);
            b.Property(x => x.CodeHash).HasMaxLength(64).IsFixedLength().IsRequired();
            b.HasIndex(x => new { x.VendorUserId, x.CodeHash }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
