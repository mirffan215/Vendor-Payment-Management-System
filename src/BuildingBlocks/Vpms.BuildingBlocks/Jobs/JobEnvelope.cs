using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Jobs;

/// <summary>Every queued job carries its tenant and correlation id.</summary>
public sealed record JobEnvelope<T>(Guid TenantId, string CorrelationId, T Payload);

/// <summary>Raised for messages that must go to the dead-letter queue and never be retried.</summary>
public sealed class DeadLetterException(string reason) : Exception(reason);

public static class TenantJobRunner
{
    /// <summary>Establishes the tenant for the scope, or dead-letters the message when it carries none.</summary>
    public static async Task RunAsync<T>(JobEnvelope<T>? envelope, TenantContext context,
        Func<T, Task> handler)
    {
        if (envelope is null || envelope.TenantId == Guid.Empty)
        {
            throw new DeadLetterException("Job message has no tenant.");
        }

        context.SetTenant(envelope.TenantId);
        await handler(envelope.Payload);
    }
}
