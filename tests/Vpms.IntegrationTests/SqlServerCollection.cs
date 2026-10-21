using Vpms.TestSupport;

namespace Vpms.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
