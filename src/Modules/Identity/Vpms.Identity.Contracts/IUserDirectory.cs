namespace Vpms.Identity.Contracts;

public sealed record UserSummary(Guid Id, string Email, string DisplayName, string Status, string Locale = "en", string Calendar = "gregorian");

public sealed record UserPreferencesDto(string Locale, string Calendar);

/// <summary>A person's own language and calendar. Kept on the server so notifications, reports and the next sign-in use the same choice.</summary>
public interface IUserPreferences
{
    Task<UserPreferencesDto> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Changes the signed-in user's own preferences.</summary>
    Task<UserPreferencesDto> SetMineAsync(string locale, string calendar, CancellationToken ct = default);
}

/// <summary>Lets other modules check or display users of the current tenant without touching identity tables.</summary>
public interface IUserDirectory
{
    Task<bool> ExistsAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, UserSummary>> GetAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);
}
