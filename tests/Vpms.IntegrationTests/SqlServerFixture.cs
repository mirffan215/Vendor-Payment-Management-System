using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Vpms.Migrator;

namespace Vpms.IntegrationTests;

/// <summary>One SQL Server container per test run; each test class gets its own migrated database.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string AppLogin = "vpms_app_login";
    private const string AppPassword = "App_Login_Pwd_1!";
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates a database, migrates it as the owner, and adds a restricted app user to the app role.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "vpms_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(_container.GetConnectionString());

        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, $"CREATE DATABASE [{name}]");
            await Exec(conn, $"""
                IF SUSER_ID('{AppLogin}') IS NULL CREATE LOGIN [{AppLogin}] WITH PASSWORD = '{AppPassword}', CHECK_POLICY = OFF;
                """);
        }

        var owner = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = name };
        await DatabaseMigrator.MigrateAsync(owner.ConnectionString);

        await using (var conn = new SqlConnection(owner.ConnectionString))
        {
            await conn.OpenAsync();
            await Exec(conn, $"CREATE USER [{AppLogin}] FOR LOGIN [{AppLogin}]; ALTER ROLE vpms_app ADD MEMBER [{AppLogin}];");
        }

        var app = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = name, UserID = AppLogin, Password = AppPassword,
        };
        return new TestDatabase(owner.ConnectionString, app.ConnectionString);
    }

    private static async Task Exec(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed record TestDatabase(string OwnerConnectionString, string AppConnectionString);

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
