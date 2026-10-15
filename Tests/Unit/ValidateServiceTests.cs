using Cli.Domain;
using Cli.Services;
using Xunit;

namespace Tests.Unit;

public class ValidateServiceTests
{
    private readonly ValidateService _service = new();

    [Fact]
    public async Task IsValid_EmptyJobs_ReturnsError()
    {
        var pipeline = new Pipeline
        {
            Name = "test",
            Jobs = [],
            Variables = []
        };

        var errors = (await _service.IsValid(pipeline)).ToList();

        Assert.Single(errors);
        Assert.Contains("Pipeline must contain at least one job", errors[0]);
    }

    [Fact]
    public async Task IsValid_JobWithNoSteps_ReturnsError()
    {
        var pipeline = new Pipeline
        {
            Name = "test",
            Jobs = new Dictionary<string, Job>
            {
                ["build"] = new Job
                {
                    DependsOn = [],
                    Env = [],
                    Steps = []
                }
            },
            Variables = []
        };

        var errors = (await _service.IsValid(pipeline)).ToList();

        Assert.Single(errors);
        Assert.Contains("Job must contain at leasst one step", errors[0]);
    }

    [Fact]
    public async Task IsValid_StepWithNoRun_ReturnsError()
    {
        var pipeline = new Pipeline
        {
            Name = "test",
            Jobs = new Dictionary<string, Job>
            {
                ["build"] = new Job
                {
                    DependsOn = [],
                    Env = [],
                    Steps = [new Step { Name = "step1" }]
                }
            },
            Variables = []
        };

        var errors = (await _service.IsValid(pipeline)).ToList();

        Assert.Single(errors);
        Assert.Contains("Step must contain a 'run' command", errors[0]);
    }

    [Fact]
    public async Task IsValid_MissingDependency_ReturnsError()
    {
        var pipeline = new Pipeline
        {
            Name = "test",
            Jobs = new Dictionary<string, Job>
            {
                ["build"] = new Job
                {
                    DependsOn = ["deploy"],
                    Env = [],
                    Steps = [new Step { Name = "step1", Run = "echo hello" }]
                }
            },
            Variables = []
        };

        var errors = (await _service.IsValid(pipeline)).ToList();

        Assert.Single(errors);
        Assert.Contains("Job 'deploy' does not exist", errors[0]);
    }

    [Fact]
    public async Task IsValid_ValidPipeline_ReturnsNoErrors()
    {
        var pipeline = new Pipeline
        {
            Name = "test",
            Jobs = new Dictionary<string, Job>
            {
                ["build"] = new Job
                {
                    DependsOn = [],
                    Env = [],
                    Steps = [new Step { Name = "compile", Run = "dotnet build" }]
                },
                ["test"] = new Job
                {
                    DependsOn = ["build"],
                    Env = [],
                    Steps = [new Step { Name = "test", Run = "dotnet test" }]
                }
            },
            Variables = []
        };

        var errors = (await _service.IsValid(pipeline)).ToList();

        Assert.Empty(errors);
    }
}
