using Railway.Engine;

using Microsoft.Extensions.DependencyInjection;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

public class EngineInitializerTests
{
    [Fact]
    public async Task InitializeEngineAsync_CreatesDatabaseFile()
    {
        using var temp = new TempDirectory();

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = temp.Path });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(temp.File("data.db")), $"Expected data.db to be created in {temp.Path}");
    }

    [Fact]
    public async Task InitializeEngineAsync_CreatesMissingDatabaseDirectory()
    {
        using var temp = new TempDirectory();
        var nestedPath = temp.File("nested");

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = nestedPath });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(nestedPath), $"Expected directory {nestedPath} to be created");
        Assert.True(File.Exists(Path.Combine(nestedPath, "data.db")));
    }

    [Fact]
    public async Task InitializeEngineAsync_SkipsCreation_WhenInitializeDatabaseIsFalse()
    {
        using var temp = new TempDirectory();

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = temp.Path, InitializeDatabase = false });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(temp.File("data.db")));
    }
}
