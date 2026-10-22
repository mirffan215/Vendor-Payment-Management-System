using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vpms.Audit.Infrastructure;
using Vpms.BuildingBlocks.Tenancy;
using Vpms.CustomerAdmin.Infrastructure;
using Vpms.Documents.Infrastructure;
using Vpms.Identity.Infrastructure;
using Vpms.Invoices.Infrastructure;
using Vpms.Workflow.Infrastructure;
using Vpms.VendorPortal.Infrastructure;
using Vpms.Vendors.Infrastructure;

namespace Vpms.Migrator;

/// <summary>Applies every module's migrations. Runs with the migrator identity, never the app identity.</summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        var tenant = new TenantContext();
        using var _ = tenant.BeginPlatformScope();

        await using (var ctx = new CustomerAdminDbContext(Options<CustomerAdminDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new AuditDbContext(Options<AuditDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new DocumentsDbContext(Options<DocumentsDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new VendorsDbContext(Options<VendorsDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new InvoicesDbContext(Options<InvoicesDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new WorkflowDbContext(Options<WorkflowDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new PortalDbContext(Options<PortalDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }

        await using (var ctx = new IdentityDbContext(Options<IdentityDbContext>(connectionString), tenant))
        {
            await ctx.Database.MigrateAsync(ct);
        }
    }

    /// <summary>
    /// Creates the restricted application login and puts it in the vpms_app role (created by the migrations).
    /// The application must connect with this login: the owner/migrator identity is not bound by the audit DENY rules.
    /// </summary>
    public static async Task EnsureAppLoginAsync(string ownerConnectionString, string login, string password,
        CancellationToken ct = default)
    {
        if (!Regex.IsMatch(login, "^[A-Za-z0-9_]{1,64}$"))
        {
            throw new ArgumentException("Login name may only contain letters, digits and underscores.", nameof(login));
        }

        await using var conn = new SqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);

        const string createLogin = """
            IF SUSER_ID(@login) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login)
                    + N' WITH PASSWORD = ' + QUOTENAME(@password, '''') + N', CHECK_POLICY = ON;';
                EXEC (@sql);
            END
            """;
        await Execute(conn, createLogin, login, password, ct);

        const string createUser = """
            IF DATABASE_PRINCIPAL_ID(@login) IS NULL
            BEGIN
                DECLARE @create nvarchar(max) = N'CREATE USER ' + QUOTENAME(@login) + N' FOR LOGIN ' + QUOTENAME(@login) + N';';
                EXEC (@create);
            END
            DECLARE @add nvarchar(max) = N'ALTER ROLE [vpms_app] ADD MEMBER ' + QUOTENAME(@login) + N';';
            EXEC (@add);
            """;
        await Execute(conn, createUser, login, null, ct);
    }

    private static async Task Execute(SqlConnection conn, string sql, string login, string? password, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@login", login);
        if (password is not null)
        {
            cmd.Parameters.AddWithValue("@password", password);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DbContextOptions<T> Options<T>(string cs) where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseSqlServer(cs).Options;
}
