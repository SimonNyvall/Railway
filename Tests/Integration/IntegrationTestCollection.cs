using Xunit;
using Tests.Infrastructure;

namespace Tests.Integration;

[CollectionDefinition("Integration Tests", DisableParallelization = true)]
public class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>
{
}

public class IntegrationTestFixture : IDisposable
{
    public string DbPath { get; }

    public IntegrationTestFixture()
    {
        DbPath = Path.Combine(Path.GetTempPath(), "railway_test_db_" + Guid.NewGuid());
        Directory.CreateDirectory(DbPath);
        // Register services in LifetimeService for command DI - once for all tests in collection
        TestServiceRegistrar.RegisterServices(DbPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(DbPath))
        {
            Directory.Delete(DbPath, true);
        }
    }
}