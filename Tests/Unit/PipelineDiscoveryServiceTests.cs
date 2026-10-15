using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Services;
using Xunit;

namespace Tests.Unit;

public class PipelineDiscoveryServiceTests
{
    [Fact]
    public async Task Discover_NoPipelinesDirectory_ReturnsEmptyList()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            Directory.CreateDirectory(railwayPath);
            // Don't create pipelines directory

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Discover_ValidPipelineFile_ReturnsDiscoveredPipeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            var pipelinesPath = Path.Combine(railwayPath, "pipelines");
            Directory.CreateDirectory(pipelinesPath);

            var pipelineYaml = """
                name: build-pipeline
                jobs:
                  build:
                    steps:
                      - name: compile
                        run: dotnet build
                """;

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "build.yaml"), pipelineYaml, TestContext.Current.CancellationToken);

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Single(results);
            Assert.Equal("build.yaml", results[0].FileName);
            Assert.Equal("build-pipeline", results[0].Name);
            Assert.True(results[0].IsValid);
            Assert.Empty(results[0].Errors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Discover_InvalidYaml_ReturnsInvalidPipeline()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            var pipelinesPath = Path.Combine(railwayPath, "pipelines");
            Directory.CreateDirectory(pipelinesPath);

            var invalidYaml = "invalid: yaml: content: [";
            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "invalid.yaml"), invalidYaml, TestContext.Current.CancellationToken);

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Single(results);
            Assert.Equal("invalid.yaml", results[0].FileName);
            Assert.False(results[0].IsValid);
            Assert.NotEmpty(results[0].Errors);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Discover_MultiplePipelineFiles_ReturnsAll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            var pipelinesPath = Path.Combine(railwayPath, "pipelines");
            Directory.CreateDirectory(pipelinesPath);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "build.yaml"), """
                name: build
                jobs:
                  build:
                    steps:
                      - run: dotnet build
                """, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "test.yaml"), """
                name: test
                jobs:
                  test:
                    steps:
                      - run: dotnet test
                """, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "deploy.yaml"), """
                name: deploy
                jobs:
                  deploy:
                    steps:
                      - run: echo deploy
                """, TestContext.Current.CancellationToken);

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Equal(3, results.Count);
            Assert.Contains(results, r => r.FileName == "build.yaml");
            Assert.Contains(results, r => r.FileName == "test.yaml");
            Assert.Contains(results, r => r.FileName == "deploy.yaml");
            Assert.All(results, r => Assert.True(r.IsValid));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Discover_NestedDirectories_FindsAllPipelines()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            var pipelinesPath = Path.Combine(railwayPath, "pipelines");
            var nestedPath = Path.Combine(pipelinesPath, "ci");
            Directory.CreateDirectory(nestedPath);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "root.yaml"), """
                name: root
                jobs:
                  job:
                    steps:
                      - run: echo root
                """, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(nestedPath, "nested.yaml"), """
                name: nested
                jobs:
                  job:
                    steps:
                      - run: echo nested
                """, TestContext.Current.CancellationToken);

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Equal(2, results.Count);
            Assert.Contains(results, r => r.FileName == "root.yaml");
            Assert.Contains(results, r => r.FileName == "nested.yaml");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Discover_SortsFilesAlphabetically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            var railwayPath = Path.Combine(tempDir, ".railway");
            var pipelinesPath = Path.Combine(railwayPath, "pipelines");
            Directory.CreateDirectory(pipelinesPath);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "z-last.yaml"), """
                name: last
                jobs:
                  job:
                    steps:
                      - run: echo last
                """, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "a-first.yaml"), """
                name: first
                jobs:
                  job:
                    steps:
                      - run: echo first
                """, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(Path.Combine(pipelinesPath, "m-middle.yaml"), """
                name: middle
                jobs:
                  job:
                    steps:
                      - run: echo middle
                """, TestContext.Current.CancellationToken);

            var results = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

            Assert.Equal(3, results.Count);
            Assert.Equal("a-first.yaml", results[0].FileName);
            Assert.Equal("m-middle.yaml", results[1].FileName);
            Assert.Equal("z-last.yaml", results[2].FileName);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
