namespace Vpms.Identity.Contracts;

/// <summary>Which legal entities a permission applies to for a user. AllEntities means tenant-wide.</summary>
public sealed record PermissionScope(bool AllEntities, IReadOnlySet<Guid> EntityIds)
{
    public static readonly PermissionScope None = new(false, new HashSet<Guid>());

    public bool Allows(Guid? legalEntityId) =>
        AllEntities || (legalEntityId is { } id && EntityIds.Contains(id));
}

public interface IPermissionEvaluator
{
    /// <summary>Tenant-wide check when legalEntityId is null (needs an unscoped assignment); entity check otherwise.</summary>
    Task<bool> HasAsync(Guid userId, string permission, Guid? legalEntityId = null, CancellationToken ct = default);

    Task<PermissionScope> GetScopeAsync(Guid userId, string permission, CancellationToken ct = default);
}
