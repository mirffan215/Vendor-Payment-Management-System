using Vpms.BuildingBlocks.Audit;

namespace Vpms.BuildingBlocks.Security;

/// <summary>The authenticated actor of the current request. Services that need "who is doing this" take this, never an id from the client.</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}

public sealed class CurrentUser(AuditContext context) : ICurrentUser
{
    public Guid UserId => context.ActorId ?? throw new InvalidOperationException("No authenticated user in this scope.");
}
