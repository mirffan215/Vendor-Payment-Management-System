using Vpms.TestSupport;

namespace Vpms.ApiTests;

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
