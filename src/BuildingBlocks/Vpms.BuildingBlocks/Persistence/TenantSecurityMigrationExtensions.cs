using Microsoft.EntityFrameworkCore.Migrations;

namespace Vpms.BuildingBlocks.Persistence;

public static class TenantSecurityMigrationExtensions
{
    public const string AppRole = "vpms_app";

    /// <summary>Creates the shared RLS predicate and the app database role (idempotent).</summary>
    public static MigrationBuilder EnsureTenantSecurityInfrastructure(this MigrationBuilder mb)
    {
        mb.Sql("IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');");
        mb.Sql($"IF DATABASE_PRINCIPAL_ID(N'{AppRole}') IS NULL EXEC(N'CREATE ROLE {AppRole}');");
        mb.Sql("""
            IF OBJECT_ID(N'security.fn_TenantPredicate') IS NULL
            EXEC(N'CREATE FUNCTION security.fn_TenantPredicate(@TenantId uniqueidentifier)
                RETURNS TABLE WITH SCHEMABINDING AS
                RETURN SELECT 1 AS allowed
                WHERE @TenantId = CAST(SESSION_CONTEXT(N''TenantId'') AS uniqueidentifier)
                   OR CAST(ISNULL(SESSION_CONTEXT(N''IsPlatform''), 0) AS int) = 1');
            """);
        return mb;
    }

    /// <summary>Filter and block predicates so a connection can only see and write its own tenant's rows.</summary>
    public static MigrationBuilder EnableTenantRowLevelSecurity(this MigrationBuilder mb, string schema, string table,
        string column = "TenantId")
    {
        var policy = $"TenantPolicy_{schema}_{table}";
        var target = $"[{schema}].[{table}]";
        mb.Sql($"""
            CREATE SECURITY POLICY [security].[{policy}]
                ADD FILTER PREDICATE [security].[fn_TenantPredicate]([{column}]) ON {target},
                ADD BLOCK PREDICATE [security].[fn_TenantPredicate]([{column}]) ON {target} AFTER INSERT,
                ADD BLOCK PREDICATE [security].[fn_TenantPredicate]([{column}]) ON {target} AFTER UPDATE,
                ADD BLOCK PREDICATE [security].[fn_TenantPredicate]([{column}]) ON {target} BEFORE UPDATE,
                ADD BLOCK PREDICATE [security].[fn_TenantPredicate]([{column}]) ON {target} BEFORE DELETE
                WITH (STATE = ON);
            """);
        return mb;
    }

    public static MigrationBuilder DisableTenantRowLevelSecurity(this MigrationBuilder mb, string schema, string table)
    {
        mb.Sql($"DROP SECURITY POLICY IF EXISTS [security].[TenantPolicy_{schema}_{table}];");
        return mb;
    }

    /// <summary>Full data access for the application role on a module schema (not used for append-only schemas).</summary>
    public static MigrationBuilder GrantAppRoleOnSchema(this MigrationBuilder mb, string schema)
    {
        mb.Sql($"GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[{schema}] TO [{AppRole}];");
        return mb;
    }
}
