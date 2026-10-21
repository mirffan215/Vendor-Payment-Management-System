namespace Vpms.BuildingBlocks.Security;

/// <summary>Raised when an action would break maker-checker or segregation of duties. Maps to HTTP 403.</summary>
public sealed class SegregationOfDutiesException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

/// <summary>Maker-checker: the person who prepared something may never be the one who approves it.</summary>
public static class MakerCheckerGuard
{
    public static void EnsureDifferent(Guid maker, Guid checker, string action)
    {
        if (maker == Guid.Empty || checker == Guid.Empty)
        {
            throw new ArgumentException("Maker and checker must be identified.");
        }

        if (maker == checker)
        {
            throw new SegregationOfDutiesException("maker-checker", $"Self-approval is not allowed for '{action}'.");
        }
    }
}

/// <summary>An earlier step by a named person on the same subject, e.g. ("invoice approver", userId).</summary>
public readonly record struct PriorActor(string Role, Guid UserId);

/// <summary>The actor must differ from everyone who already took a conflicting step on the same item.</summary>
public static class DifferentPersonRule
{
    public static void Ensure(Guid actor, string action, IEnumerable<PriorActor> priorActors)
    {
        foreach (var prior in priorActors)
        {
            if (prior.UserId == actor)
            {
                throw new SegregationOfDutiesException("different-person",
                    $"'{action}' must be performed by someone other than the {prior.Role}.");
            }
        }
    }
}

public readonly record struct PermissionConflict(string First, string Second);

/// <summary>
/// Pairs of permissions one person must not hold for the same scope. Checked when roles are assigned, and the
/// same pairs are enforced again at decision time by the guards above.
/// </summary>
public sealed class ConflictingPermissionsMatrix(IEnumerable<PermissionConflict> conflicts)
{
    private readonly List<PermissionConflict> _conflicts = conflicts.ToList();

    public IReadOnlyList<PermissionConflict> Conflicts => _conflicts;

    public IReadOnlyList<PermissionConflict> FindConflicts(IReadOnlySet<string> permissions) =>
        _conflicts.Where(c => permissions.Contains(c.First) && permissions.Contains(c.Second)).ToList();

    public void EnsureNoConflicts(IReadOnlySet<string> permissions)
    {
        var found = FindConflicts(permissions);
        if (found.Count > 0)
        {
            var list = string.Join("; ", found.Select(c => $"{c.First} + {c.Second}"));
            throw new SegregationOfDutiesException("conflicting-permissions",
                $"Assignment would give one person conflicting permissions: {list}.");
        }
    }
}
