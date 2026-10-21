using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.Identity.Domain;

public class Role : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public class RolePermission : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

/// <summary>Grants a role to a user, tenant-wide (LegalEntityId null) or for one legal entity.</summary>
public class RoleAssignment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public Guid AssignedBy { get; set; }
}

/// <summary>
/// A role waiting for a person who has not signed in yet (e.g. the first administrator of a new tenant). It is applied when a
/// user with that verified email is first provisioned.
/// </summary>
public class PendingRoleAssignment : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public Guid RoleId { get; set; }
    public Guid? LegalEntityId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
