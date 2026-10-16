using Railway.Engine.Domain;
using Railway.Engine.Helpers;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Round-trip tests for <see cref="PipelineHelper.Write(Pipeline, string, CancellationToken)"/>: the written file must
/// re-parse through <see cref="PipelineHelper.Parse(string)"/> and must not carry persistence-only members.
/// </summary>
public class PipelineHelperWriteTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc/>
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Write_ThenParse_RoundTripsAllSchemaFields()
    {
        var path = _temp.File("pipeline.yaml");
        var pipeline = new Pipeline
        {
            Name = "Round Trip",
            Variables = new Dictionary<string, string> { ["DOTNET_NOLOGO"] = "true" },
            Jobs = new Dictionary<string, Job>
            {
                ["prepare"] = new Job
                {
                    Steps = [new Step { Run = "dotnet restore" }],
                },
                ["build"] = new Job
                {
                    DependsOn = ["prepare"],
                    Env = new Dictionary<string, string> { ["CONFIG"] = "Release" },
                    Timeout = TimeSpan.FromMinutes(10),
                    Locks = ["pipeline"],
                    Steps =
                    [
                        new Step
                        {
                            Name = "Build",
                            Run = "dotnet build",
                            Env = new Dictionary<string, string> { ["STEP_VAR"] = "1" },
                            Timeout = TimeSpan.FromMinutes(2),
                        },
                    ],
                },
            },
        };

        await PipelineHelper.Write(pipeline, path, TestContext.Current.CancellationToken);
        var parsed = await PipelineHelper.Parse(path);

        Assert.Equal("Round Trip", parsed.Name);
        Assert.Equal("true", parsed.Variables["DOTNET_NOLOGO"]);
        Assert.Equal(new[] { "build", "prepare" }, parsed.Jobs.Keys.OrderBy(key => key).ToArray());

        var build = parsed.Jobs["build"];
        Assert.Equal(new[] { "prepare" }, build.DependsOn);
        Assert.Equal("Release", build.Env["CONFIG"]);
        Assert.Equal(TimeSpan.FromMinutes(10), build.Timeout);
        Assert.Equal(new[] { "pipeline" }, build.Locks);

        var step = Assert.Single(build.Steps);
        Assert.Equal("Build", step.Name);
        Assert.Equal("dotnet build", step.Run);
        Assert.Equal("1", step.Env["STEP_VAR"]);
        Assert.Equal(TimeSpan.FromMinutes(2), step.Timeout);

        var prepare = parsed.Jobs["prepare"];
        Assert.Empty(prepare.DependsOn);
        Assert.Null(prepare.Timeout);
        Assert.Equal(TimeSpan.Zero, Assert.Single(prepare.Steps).Timeout);
    }

    [Fact]
    public async Task Write_DoesNotEmitPersistenceOrDefaultMembers()
    {
        var path = _temp.File("minimal.yaml");
        var pipeline = new Pipeline
        {
            Id = 42,
            Path = "existing/path.yaml",
            Name = "Minimal",
            Jobs = new Dictionary<string, Job>
            {
                ["build"] = new Job
                {
                    Steps = [new Step { Run = "dotnet build" }],
                },
            },
        };

        await PipelineHelper.Write(pipeline, path, TestContext.Current.CancellationToken);
        var yaml = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.Contains("name: Minimal", yaml);
        Assert.Contains("jobs:", yaml);
        Assert.Contains("steps:", yaml);
        Assert.Contains("run: dotnet build", yaml);

        Assert.DoesNotContain("id:", yaml);
        Assert.DoesNotContain("path:", yaml);
        Assert.DoesNotContain("repository_pipelines", yaml);
        Assert.DoesNotContain("depends_on", yaml);
        Assert.DoesNotContain("locks", yaml);
        Assert.DoesNotContain("timeout", yaml);
        Assert.DoesNotContain("variables", yaml);
    }
}
