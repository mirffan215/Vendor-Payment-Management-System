using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Vpms.BuildingBlocks.Tenancy;

namespace Vpms.BuildingBlocks.Persistence;

/// <summary>
/// Pushes the tenant into SESSION_CONTEXT on every connection open so SQL Server Row-Level Security
/// enforces isolation even if an application filter is missed (ADR-0001).
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = """
        EXEC sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 0;
        EXEC sp_set_session_context @key = N'IsPlatform', @value = @platform, @read_only = 0;
        """;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = Build(connection);
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = Build(connection);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand Build(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;

        var t = cmd.CreateParameter();
        t.ParameterName = "@tenantId";
        t.Value = (object?)tenant.TenantId ?? DBNull.Value;
        cmd.Parameters.Add(t);

        var p = cmd.CreateParameter();
        p.ParameterName = "@platform";
        p.Value = tenant.IsPlatform ? 1 : 0;
        cmd.Parameters.Add(p);
        return cmd;
    }
}
