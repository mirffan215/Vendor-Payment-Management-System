namespace Vpms.Identity.Contracts;

/// <summary>Resolves approver roles in a workflow definition to people, without exposing identity tables.</summary>
public interface IApproverDirectory
{
    /// <summary>Active users who hold each named role for the legal entity (an entity assignment or a tenant-wide one).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> GetRoleHoldersAsync(Guid legalEntityId, IEnumerable<string> roleNames, CancellationToken ct = default);

    /// <summary>All active users who hold the permission for the legal entity (an entity assignment or a tenant-wide one).</summary>
    Task<IReadOnlyList<Guid>> ListPermissionHoldersAsync(Guid legalEntityId, string permission, CancellationToken ct = default);

    /// <summary>The subset of users who hold the permission for the legal entity.</summary>
    Task<IReadOnlySet<Guid>> FilterByPermissionAsync(Guid legalEntityId, string permission, IEnumerable<Guid> userIds, CancellationToken ct = default);
}
