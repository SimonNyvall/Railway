using Railway.Engine.Domain;

namespace Tests.Infrastructure;

/// <summary>
/// Builds in-memory <see cref="Run"/> graphs for tests of the run-related tools and services.
/// </summary>
public static class RunTestData
{
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    /// <summary>
    /// Creates a succeeded run without jobs or steps.
    /// </summary>
    /// <param name="id">The run identifier.</param>
    /// <returns>The run.</returns>
    public static Run Run(int id) => new()
    {
        Id = id,
        PipelineFileName = "build.yaml",
        PipelineName = "build-pipeline",
        Status = RunStatus.Succeeded,
        ExitCode = 0,
        StartedAt = Started,
        FinishedAt = Started.AddSeconds(1),
    };

    /// <summary>
    /// Creates a succeeded run with a single job that contains a single step.
    /// </summary>
    /// <param name="id">The run identifier.</param>
    /// <returns>The run, with its jobs and steps populated.</returns>
    public static Run DetailedRun(int id)
    {
        var run = Run(id);
        var job = new RunJob { Name = "build", Status = RunStatus.Succeeded, ExitCode = 0 };
        job.Steps.Add(new RunStep
        {
            Name = "compile",
            Command = "dotnet build",
            Status = RunStatus.Succeeded,
            ExitCode = 0,
            Output = "ok",
        });
        run.Jobs.Add(job);
        return run;
    }
}
