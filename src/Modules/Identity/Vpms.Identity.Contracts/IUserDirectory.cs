namespace Vpms.Identity.Contracts;

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Status);

/// <summary>Lets other modules check or display users of the current tenant without touching identity tables.</summary>
public interface IUserDirectory
{
    Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
}
