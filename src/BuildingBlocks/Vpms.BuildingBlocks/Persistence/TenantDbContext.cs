using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Persistence;

/// <summary>
/// Base context: applies the tenant query filter to every <see cref="ITenantOwned"/> entity and stamps/verifies
/// TenantId on save. Derived contexts must call <c>base.OnModelCreating</c> AFTER configuring their entities.
/// </summary>
public abstract class TenantDbContext(DbContextOptions options, ITenantContext tenant, VendorContext? vendor = null) : DbContext(options)
{
    public const string TenantFilterName = "TenantScope";
    public const string VendorFilterName = "VendorScope";

    protected Guid CurrentTenantId => tenant.TenantId ?? Guid.Empty;

    /// <summary>Set only in vendor (portal) sessions; null for staff and background scopes.</summary>
    protected Guid? CurrentVendorId => vendor?.VendorId;

    /// <summary>What vendor-owned queries are limited to; differs from <see cref="CurrentVendorId"/> only inside a cross-vendor check.</summary>
    protected Guid? FilterVendorId => vendor?.FilterVendorId;
    protected bool IsPlatformScope => tenant.IsPlatform;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var method = typeof(TenantDbContext).GetMethod(nameof(ApplyTenantFilter),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && e.BaseType is null
                                 && !e.IsOwned()).ToList())
        {
            method.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }

        var vendorMethod = typeof(TenantDbContext).GetMethod(nameof(ApplyVendorFilter),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(e => typeof(IVendorOwned).IsAssignableFrom(e.ClrType) && e.BaseType is null && !e.IsOwned()).ToList())
        {
            vendorMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    // A named filter, so it is combined with the tenant filter and cannot be dropped by a module re-declaring its own filter.
    private void ApplyVendorFilter<T>(ModelBuilder modelBuilder) where T : class, IVendorOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(VendorFilterName, e =>
            FilterVendorId == null || EF.Property<Guid>(e, nameof(IVendorOwned.VendorId)) == FilterVendorId);

    /// <summary>For the vendor master row itself, whose key is the vendor id. Call from the module's OnModelCreating.</summary>
    protected void ApplyVendorFilterOnKey<T>(ModelBuilder modelBuilder, string keyProperty = "Id") where T : class =>
        modelBuilder.Entity<T>().HasQueryFilter(VendorFilterName, e =>
            FilterVendorId == null || EF.Property<Guid>(e, keyProperty) == FilterVendorId);

    private void ApplyTenantFilter<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned
    {
        modelBuilder.Entity<T>().HasQueryFilter(TenantFilterName, e =>
            EF.Property<Guid>(e, nameof(ITenantOwned.TenantId)) == CurrentTenantId || IsPlatformScope);
        modelBuilder.Entity<T>().HasIndex(nameof(ITenantOwned.TenantId));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceTenant()
    {
        EnforceVendor();

        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached))
        {
            if (tenant.IsPlatform)
            {
                if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
                {
                    throw new TenantViolationException("Platform writes must set TenantId explicitly.");
                }

                continue;
            }

            var current = tenant.RequireTenantId();

            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId == Guid.Empty)
                {
                    entry.Entity.TenantId = current;
                }
            }
            else if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified
                     && entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue is Guid original && original != entry.Entity.TenantId)
            {
                throw new TenantViolationException("TenantId cannot be changed.");
            }

            if (entry.Entity.TenantId != current)
            {
                throw new TenantViolationException(
                    $"Entity {entry.Metadata.ClrType.Name} belongs to a different tenant than the current scope.");
            }
        }
    }

    private void EnforceVendor()
    {
        if (CurrentVendorId is not { } vendorId) return;

        foreach (var entry in ChangeTracker.Entries<IVendorOwned>().Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached))
        {
            if (entry.Entity.VendorId != vendorId)
            {
                throw new TenantViolationException($"Entity {entry.Metadata.ClrType.Name} belongs to a different vendor than the current session.");
            }
        }
    }
}
