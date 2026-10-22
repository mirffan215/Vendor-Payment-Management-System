using Microsoft.EntityFrameworkCore;
using Vpms.BuildingBlocks.Persistence;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.Identity.Domain;

namespace Vpms.Identity.Infrastructure;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<PendingRoleAssignment> PendingRoleAssignments => Set<PendingRoleAssignment>();
    public DbSet<IdentityProviderConfig> IdentityProviders => Set<IdentityProviderConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("User");
            b.HasKey(x => x.Id);
            b.Property(x => x.ExternalSubject).HasMaxLength(256).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Locale).HasMaxLength(10).IsRequired();
            b.Property(x => x.Calendar).HasMaxLength(10).IsRequired().HasDefaultValue("gregorian");
            b.Property(x => x.Status).HasMaxLength(20).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.ProviderId, x.ExternalSubject }).IsUnique();
        });

        modelBuilder.Entity<IdentityProviderConfig>(b =>
        {
            b.ToTable("IdentityProvider");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Authority).HasMaxLength(500).IsRequired();
            b.Property(x => x.ClientId).HasMaxLength(200).IsRequired();
            b.Property(x => x.ClientSecretRef).HasMaxLength(200).IsRequired();
            b.Property(x => x.Scopes).HasMaxLength(500).IsRequired();
            b.Property(x => x.AllowedEmailDomains).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.ToTable("Role");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RolePermission>(b =>
        {
            b.ToTable("RolePermission");
            b.HasKey(x => x.Id);
            b.Property(x => x.Permission).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.RoleId, x.Permission }).IsUnique();
        });

        modelBuilder.Entity<RoleAssignment>(b =>
        {
            b.ToTable("RoleAssignment");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.RoleId, x.LegalEntityId }).IsUnique();
        });

        modelBuilder.Entity<PendingRoleAssignment>(b =>
        {
            b.ToTable("PendingRoleAssignment");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Email, x.RoleId, x.LegalEntityId }).IsUnique();
        });

        base.OnModelCreating(modelBuilder);
    }
}
