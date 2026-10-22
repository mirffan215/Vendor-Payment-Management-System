using Vpms.Identity.Domain;

namespace Vpms.Identity.Application;

public sealed record RoleDto(Guid Id, string Name, bool IsSystem, IReadOnlyList<string> Permissions, int AssignmentCount);

public sealed record RoleAssignmentDto(Guid Id, Guid UserId, Guid RoleId, string RoleName, Guid? LegalEntityId, DateTimeOffset AssignedAt, Guid AssignedBy);

public sealed record PendingAssignmentDto(Guid Id, string Email, Guid RoleId, string RoleName, Guid? LegalEntityId);

public interface IRoleAdminService
{
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default);

    Task<RoleDto> CreateRoleAsync(string name, IReadOnlyCollection<string> permissions, CancellationToken ct = default);

    /// <summary>Only custom roles that nobody holds can be changed; otherwise create a new role and move people over.</summary>
    Task<RoleDto> UpdateRolePermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken ct = default);

    Task DeleteRoleAsync(Guid roleId, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignmentDto>> ListAssignmentsAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>Assigns a role to someone else. Self-assignment is refused; segregation of duties is checked.</summary>
    Task<RoleAssignmentDto> AssignAsync(Guid userId, Guid roleId, Guid? legalEntityId, CancellationToken ct = default);

    Task RevokeAsync(Guid assignmentId, CancellationToken ct = default);

    Task<PendingAssignmentDto> AddPendingAssignmentAsync(string email, Guid roleId, Guid? legalEntityId, CancellationToken ct = default);

    Task<IReadOnlyList<PendingAssignmentDto>> ListPendingAssignmentsAsync(CancellationToken ct = default);
}

public sealed record UserDto(Guid Id, string Email, string DisplayName, string Status, DateTimeOffset? LastLoginAt, IReadOnlyList<string> Roles);

public interface IUserAdminService
{
    Task<IReadOnlyList<UserDto>> ListAsync(string? search, int skip, int take, CancellationToken ct = default);

    /// <summary>Disabling takes effect on the user's next request. You cannot disable yourself or the last administrator.</summary>
    Task<UserDto> SetStatusAsync(Guid userId, bool active, CancellationToken ct = default);

    /// <summary>
    /// Removes a former user's name, e-mail address and sign-in link from the user record, keeping the id, so the audit trail (which holds only ids)
    /// no longer identifies the person. Only for a disabled user, and never yourself. Cannot be undone.
    /// </summary>
    Task AnonymiseAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Ends every session the person has now ("sign out everywhere"). A person may do it for themselves; an administrator may do it for anyone. Audited.</summary>
    Task RevokeSessionsAsync(Guid userId, CancellationToken ct = default);
}

public sealed record ProviderDto(
    Guid Id, IdentityProviderKind Kind, string DisplayName, string Authority, string ClientId, string ClientSecretRef, string Scopes,
    bool Enabled, bool AutoProvision, IReadOnlyList<string> AllowedEmailDomains, bool SecretAvailable);

public sealed record SaveProvider(
    IdentityProviderKind Kind, string DisplayName, string Authority, string ClientId, string ClientSecretRef, string? Scopes,
    bool AutoProvision, IReadOnlyList<string>? AllowedEmailDomains);

public interface IProviderAdminService
{
    Task<IReadOnlyList<ProviderDto>> ListAsync(CancellationToken ct = default);

    Task<ProviderDto> CreateAsync(SaveProvider request, CancellationToken ct = default);

    Task<ProviderDto> UpdateAsync(Guid id, SaveProvider request, CancellationToken ct = default);

    Task<ProviderDto> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);
}

/// <summary>Told when a provider's settings change so cached runtime sign-in schemes can be dropped.</summary>
public interface IProviderChangeNotifier
{
    void ProviderChanged(Guid providerId);
}

public sealed class NoOpProviderChangeNotifier : IProviderChangeNotifier
{
    public void ProviderChanged(Guid providerId)
    {
    }
}
