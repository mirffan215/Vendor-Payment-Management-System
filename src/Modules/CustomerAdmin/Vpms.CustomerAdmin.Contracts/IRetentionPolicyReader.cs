using Vpms.BuildingBlocks.Jobs;

namespace Vpms.CustomerAdmin.Contracts;

/// <summary>The tenant's active retention policy and legal holds, as other modules and the retention job need them.</summary>
public interface IRetentionPolicyReader
{
    Task<RetentionContext> ContextAsync(CancellationToken ct = default);
}
