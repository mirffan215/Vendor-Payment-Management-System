using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Application;

namespace Vpms.Audit.Infrastructure;

public sealed class AuditQueryService(AuditDbContext db) : IAuditQueryService
{
    public async Task<AuditPage> QueryAsync(AuditQuery q, IReadOnlySet<Guid>? entityScope, CancellationToken ct = default)
    {
        var limit = Math.Clamp(q.Limit, 1, AuditQuery.MaxLimit);

        // The tenant query filter (and RLS) already restrict this to the caller's tenant.
        IQueryable<Vpms.Audit.Domain.AuditEvent> events = db.Events.AsNoTracking();

        if (entityScope is not null)
        {
            events = events.Where(e => e.LegalEntityId != null && entityScope.Contains(e.LegalEntityId.Value));
        }

        if (q.LegalEntityId is { } entity) events = events.Where(e => e.LegalEntityId == entity);
        if (q.ActorId is { } actor) events = events.Where(e => e.ActorId == actor);
        if (!string.IsNullOrWhiteSpace(q.Action)) events = events.Where(e => e.Action == q.Action);
        if (!string.IsNullOrWhiteSpace(q.EntityType)) events = events.Where(e => e.EntityType == q.EntityType);
        if (!string.IsNullOrWhiteSpace(q.EntityId)) events = events.Where(e => e.EntityId == q.EntityId);
        if (!string.IsNullOrWhiteSpace(q.CorrelationId)) events = events.Where(e => e.CorrelationId == q.CorrelationId);
        if (q.From is { } from) events = events.Where(e => e.OccurredAt >= from);
        if (q.To is { } to) events = events.Where(e => e.OccurredAt <= to);
        if (q.AfterSequence is { } after) events = events.Where(e => e.Sequence > after);

        var items = await events.OrderBy(e => e.Sequence).Take(limit + 1).ToListAsync(ct);
        var hasMore = items.Count > limit;
        if (hasMore)
        {
            items.RemoveAt(limit);
        }

        return new AuditPage(items, hasMore ? items[^1].Sequence : null);
    }
}
