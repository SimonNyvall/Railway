using ModelContextProtocol;
using Railway.Engine.Domain;
using Railway.Mcp.Tools;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RunEventProgressAdapter"/>: milestone events are always
/// reported, standard output and error are suppressed unless output is included, progress
/// values increase monotonically, and the total is left unknown.
/// </summary>
public class RunEventProgressAdapterTests
{
    private static RunEvent Milestone(RunEventKind kind) => new()
    {
        Kind = kind,
        RunId = 7,
        JobName = "build",
        StepName = "compile",
        Text = "dotnet build",
        ExitCode = 0,
        Status = RunStatus.Succeeded
    };

    private static (RunEventProgressAdapter Adapter, RecordingProgress<ProgressNotificationValue> Progress) Create(
        bool includeOutput = false,
        bool verbose = false)
    {
        var progress = new RecordingProgress<ProgressNotificationValue>();
        return (new RunEventProgressAdapter(progress, includeOutput, verbose), progress);
    }

    [Theory]
    [InlineData(RunEventKind.RunStarted)]
    [InlineData(RunEventKind.JobStarted)]
    [InlineData(RunEventKind.StepStarted)]
    [InlineData(RunEventKind.StepCompleted)]
    [InlineData(RunEventKind.JobCompleted)]
    [InlineData(RunEventKind.RunCompleted)]
    public void MilestoneEvents_AreAlwaysReported(RunEventKind kind)
    {
        var (adapter, progress) = Create(includeOutput: false);

        adapter.Report(new RunEvent
        {
            Kind = kind,
            RunId = 7,
            JobName = "build",
            StepName = "compile",
            ExitCode = 0,
            Status = RunStatus.Succeeded,
        });

        var reported = Assert.Single(progress.Values);
        Assert.False(string.IsNullOrWhiteSpace(reported.Message));
    }

    [Theory]
    [InlineData(RunEventKind.StandardOutput, "hello")]
    [InlineData(RunEventKind.StandardError, "boom")]
    public void OutputEvents_SuppressedWhenIncludeOutputDisabled(RunEventKind kind, string text)
    {
        var (adapter, progress) = Create(includeOutput: false);

        adapter.Report(new RunEvent { Kind = kind, Text = text });

        Assert.Empty(progress.Values);
    }

    [Fact]
    public void StandardOutput_ForwardedWhenIncludeOutputEnabled()
    {
        var (adapter, progress) = Create(includeOutput: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.StandardOutput, Text = "hello" });

        Assert.Equal("hello", Assert.Single(progress.Values).Message);
    }

    [Fact]
    public void StandardError_ForwardedWithPrefixWhenIncludeOutputEnabled()
    {
        var (adapter, progress) = Create(includeOutput: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.StandardError, Text = "boom" });

        Assert.Equal("[stderr] boom", Assert.Single(progress.Values).Message);
    }

    [Fact]
    public void ProgressValues_IncreaseMonotonically()
    {
        var (adapter, progress) = Create(includeOutput: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.RunStarted, RunId = 1 });
        adapter.Report(new RunEvent { Kind = RunEventKind.StandardOutput, Text = "line" });
        adapter.Report(new RunEvent { Kind = RunEventKind.JobStarted, JobName = "build" });
        adapter.Report(new RunEvent { Kind = RunEventKind.RunCompleted, Status = RunStatus.Succeeded });

        var values = progress.Values.Select(v => v.Progress).ToArray();

        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, values);
        Assert.True(values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First));
    }

    [Fact]
    public void ReportedValues_HaveNullTotal()
    {
        var (adapter, progress) = Create(includeOutput: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.RunStarted, RunId = 1 });
        adapter.Report(new RunEvent { Kind = RunEventKind.StandardOutput, Text = "line" });
        adapter.Report(new RunEvent { Kind = RunEventKind.RunCompleted, RunId = 1, Status = RunStatus.Succeeded });

        Assert.All(progress.Values, value => Assert.Null(value.Total));
    }

    [Fact]
    public void VerboseStepStarted_IncludesCommand()
    {
        var (adapter, progress) = Create(verbose: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.StepStarted, StepName = "compile", Text = "dotnet build" });

        Assert.Contains("dotnet build", Assert.Single(progress.Values).Message);
    }

    [Fact]
    public void NonVerboseStepStarted_OmitsCommand()
    {
        var (adapter, progress) = Create(verbose: false);

        adapter.Report(new RunEvent { Kind = RunEventKind.StepStarted, StepName = "compile", Text = "dotnet build" });

        Assert.DoesNotContain("dotnet build", Assert.Single(progress.Values).Message);
    }

    [Fact]
    public void NullProgressSink_DoesNotThrow()
    {
        var adapter = new RunEventProgressAdapter(null, includeOutput: true, verbose: true);

        adapter.Report(new RunEvent { Kind = RunEventKind.RunStarted, RunId = 1 });
    }
}
