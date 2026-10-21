namespace Vpms.Identity.Application;

public interface IRoleAssignmentService
{
    /// <summary>Creates the default roles of a tenant if they do not exist yet.</summary>
    Task EnsureSystemRolesAsync(CancellationToken ct = default);

    /// <summary>Assigns a role after checking segregation of duties against everything the user already holds.</summary>
    Task AssignAsync(Guid actorUserId, Guid userId, Guid roleId, Guid? legalEntityId, CancellationToken ct = default);
}
