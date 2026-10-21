using System.Data;
using System.Data.Common;
using Vpms.Audit.Domain;
using Vpms.BuildingBlocks.Audit;

namespace Vpms.Audit.Infrastructure;

/// <summary>
/// Appends events to the tenant's hash chain inside the caller's transaction. The chain head row is locked
/// (UPDLOCK, HOLDLOCK) so concurrent writers of one tenant are serialised and the sequence has no gaps.
/// </summary>
public sealed class SqlAuditAppender(TimeProvider clock) : IAuditAppender
{
    public async Task AppendAsync(DbConnection connection, DbTransaction transaction,
        IReadOnlyList<ResolvedAuditEntry> entries, CancellationToken ct = default)
    {
        foreach (var group in entries.GroupBy(e => e.TenantId))
        {
            var (sequence, hash) = await LockHeadAsync(connection, transaction, group.Key, ct);

            foreach (var entry in group)
            {
                sequence++;
                var ev = new AuditEvent
                {
                    Id = Guid.NewGuid(), TenantId = entry.TenantId, LegalEntityId = entry.LegalEntityId, Sequence = sequence,
                    OccurredAt = clock.GetUtcNow(), ActorType = entry.ActorType, ActorId = entry.ActorId,
                    OnBehalfOfId = entry.OnBehalfOfId, Action = entry.Action, EntityType = entry.EntityType,
                    EntityId = entry.EntityId, CorrelationId = entry.CorrelationId, IpAddress = entry.IpAddress,
                    BeforeHash = entry.BeforeHash, AfterHash = entry.AfterHash, ChangeSummaryJson = entry.ChangeSummaryJson,
                    PrevHash = hash,
                };
                ev.Hash = AuditHasher.Compute(hash, ev);
                hash = ev.Hash;
                await InsertEventAsync(connection, transaction, ev, ct);
            }

            await UpdateHeadAsync(connection, transaction, group.Key, sequence, hash, ct);
        }
    }

    private async Task<(long Sequence, string Hash)> LockHeadAsync(DbConnection c, DbTransaction t, Guid tenantId, CancellationToken ct)
    {
        await using (var create = Command(c, t, """
            INSERT INTO [audit].[ChainHead] ([TenantId], [Sequence], [Hash], [UpdatedAt])
            SELECT @t, 0, @zeros, SYSDATETIMEOFFSET()
            WHERE NOT EXISTS (SELECT 1 FROM [audit].[ChainHead] WITH (UPDLOCK, HOLDLOCK) WHERE [TenantId] = @t);
            """))
        {
            Add(create, "@t", tenantId);
            Add(create, "@zeros", AuditHasher.GenesisHash);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var read = Command(c, t,
            "SELECT [Sequence], [Hash] FROM [audit].[ChainHead] WITH (UPDLOCK, HOLDLOCK) WHERE [TenantId] = @t;");
        Add(read, "@t", tenantId);
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("Audit chain head is not accessible for this tenant.");
        }

        return (reader.GetInt64(0), reader.GetString(1));
    }

    private static async Task InsertEventAsync(DbConnection c, DbTransaction t, AuditEvent e, CancellationToken ct)
    {
        await using var cmd = Command(c, t, """
            INSERT INTO [audit].[AuditEvent]
              ([Id],[TenantId],[LegalEntityId],[Sequence],[OccurredAt],[ActorType],[ActorId],[OnBehalfOfId],[Action],
               [EntityType],[EntityId],[CorrelationId],[IpAddress],[BeforeHash],[AfterHash],[ChangeSummaryJson],[PrevHash],[Hash])
            VALUES
              (@id,@tenant,@entity,@seq,@at,@actorType,@actor,@onBehalf,@action,
               @entityType,@entityId,@corr,@ip,@before,@after,@summary,@prev,@hash);
            """);
        Add(cmd, "@id", e.Id);
        Add(cmd, "@tenant", e.TenantId);
        Add(cmd, "@entity", e.LegalEntityId);
        Add(cmd, "@seq", e.Sequence);
        Add(cmd, "@at", e.OccurredAt, DbType.DateTimeOffset);
        Add(cmd, "@actorType", e.ActorType);
        Add(cmd, "@actor", e.ActorId);
        Add(cmd, "@onBehalf", e.OnBehalfOfId);
        Add(cmd, "@action", e.Action);
        Add(cmd, "@entityType", e.EntityType);
        Add(cmd, "@entityId", e.EntityId);
        Add(cmd, "@corr", e.CorrelationId);
        Add(cmd, "@ip", e.IpAddress);
        Add(cmd, "@before", e.BeforeHash);
        Add(cmd, "@after", e.AfterHash);
        Add(cmd, "@summary", e.ChangeSummaryJson);
        Add(cmd, "@prev", e.PrevHash);
        Add(cmd, "@hash", e.Hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task UpdateHeadAsync(DbConnection c, DbTransaction t, Guid tenantId, long sequence, string hash, CancellationToken ct)
    {
        await using var cmd = Command(c, t,
            "UPDATE [audit].[ChainHead] SET [Sequence] = @seq, [Hash] = @hash, [UpdatedAt] = @at WHERE [TenantId] = @t;");
        Add(cmd, "@seq", sequence);
        Add(cmd, "@hash", hash);
        Add(cmd, "@at", clock.GetUtcNow(), DbType.DateTimeOffset);
        Add(cmd, "@t", tenantId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DbCommand Command(DbConnection c, DbTransaction t, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void Add(DbCommand cmd, string name, object? value, DbType? type = null)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        if (type is { } dbType)
        {
            p.DbType = dbType;
        }

        cmd.Parameters.Add(p);
    }
}
