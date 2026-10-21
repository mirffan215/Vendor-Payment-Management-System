using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Audit;

/// <summary>Writes the audit rows into the caller's own database transaction.</summary>
public interface IAuditAppender
{
    Task AppendAsync(DbConnection connection, DbTransaction transaction, IReadOnlyList<ResolvedAuditEntry> entries,
        CancellationToken ct = default);
}

public sealed class AuditBuffer
{
    private readonly List<ResolvedAuditEntry> _entries = [];

    public void Add(ResolvedAuditEntry entry) => _entries.Add(entry);

    public IReadOnlyList<ResolvedAuditEntry> Drain()
    {
        var copy = _entries.ToList();
        _entries.Clear();
        return copy;
    }
}

/// <summary>
/// On SaveChanges, appends buffered audit entries on the context's connection and transaction. If no transaction
/// is open, one is started and committed (or rolled back on failure) together with the business changes.
/// </summary>
public sealed class AuditSaveChangesInterceptor(AuditBuffer buffer, IAuditAppender appender, ITenantContext tenant)
    : SaveChangesInterceptor
{
    private IDbContextTransaction? _owned;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        PrepareAsync(eventData, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(eventData, cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        _owned?.Commit();
        Release();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (_owned is not null)
        {
            await _owned.CommitAsync(cancellationToken);
        }

        Release();
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _owned?.Rollback();
        Release();
    }

    public override async Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (_owned is not null)
        {
            await _owned.RollbackAsync(cancellationToken);
        }

        Release();
    }

    private async Task PrepareAsync(DbContextEventData eventData, CancellationToken ct)
    {
        var context = eventData.Context;
        if (context is null)
        {
            return;
        }

        var entries = buffer.Drain();
        if (entries.Count == 0)
        {
            return;
        }

        if (!tenant.IsPlatform && entries.Any(e => e.TenantId != tenant.TenantId))
        {
            throw new TenantViolationException("Audit entry belongs to a different tenant than the current scope.");
        }

        if (context.Database.CurrentTransaction is null)
        {
            _owned = await context.Database.BeginTransactionAsync(ct);
        }

        var tx = context.Database.CurrentTransaction!.GetDbTransaction();
        await appender.AppendAsync(context.Database.GetDbConnection(), tx, entries, ct);
    }

    private void Release()
    {
        _owned?.Dispose();
        _owned = null;
    }
}
