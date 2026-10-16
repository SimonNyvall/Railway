using Railway.Engine;
using Railway.Engine.Infrastructure;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Tests.Unit;

public class EngineServiceCollectionExtensionsTests
{
    [Fact]
    public void AddEngine_ResolvesAllEngineServices()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"railway-engine-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var services = new ServiceCollection();
            services.AddEngine(new EngineOptions { DatabasePath = tempDir });

            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<IValidateService>());
            Assert.NotNull(provider.GetRequiredService<IPipelineDiscoveryService>());
            Assert.NotNull(provider.GetRequiredService<IDashboardService>());
            Assert.NotNull(provider.GetRequiredService<IRunnerService>());
            Assert.NotNull(provider.GetRequiredService<IRunHistoryService>());
            Assert.NotNull(provider.GetRequiredService<IRunHistoryQuery>());
            Assert.NotNull(provider.GetRequiredService<IPipelineConvertService>());
            Assert.NotNull(provider.GetRequiredService<IPipelineConverter>());
            Assert.NotNull(provider.GetRequiredService<RailwayDbContext>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
