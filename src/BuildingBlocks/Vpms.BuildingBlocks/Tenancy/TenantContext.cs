namespace Vpms.BuildingBlocks.Tenancy;

public interface ITenantContext
{
    /// <summary>Current tenant, or null when none has been resolved (nothing is visible then).</summary>
    Guid? TenantId { get; }

    /// <summary>True only inside an explicit, audited platform scope (see <see cref="TenantContext.BeginPlatformScope"/>).</summary>
    bool IsPlatform { get; }

    Guid RequireTenantId();
}

public sealed class TenantRequiredException()
    : InvalidOperationException("No tenant is resolved for this operation.");

public sealed class TenantViolationException(string message) : InvalidOperationException(message);

/// <summary>Scoped holder for the tenant of the current request or job.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool IsPlatform { get; private set; }

    public Guid RequireTenantId() => TenantId ?? throw new TenantRequiredException();

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (TenantId is { } existing && existing != tenantId)
        {
            throw new TenantViolationException("The tenant of the current scope cannot be changed.");
        }

        TenantId = tenantId;
    }

    /// <summary>
    /// Cross-tenant access for platform operations (login tenant lookup, schedulers, migrations).
    /// This is the only way to bypass tenant filters; callers are allow-listed by architecture tests.
    /// </summary>
    public IDisposable BeginPlatformScope()
    {
        IsPlatform = true;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        public void Dispose() => owner.IsPlatform = false;
    }
}
