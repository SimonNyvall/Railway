using Cli.Domain;
using Cli.Infrastructure;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Spectre.Console;
using System.Collections;

namespace Cli.Services;

public class RunnerService : IRunnerService
{
    private readonly RailwayDbContext _dbContext;

    public RunnerService()
    {
        _dbContext = LifetimeService.Get<RailwayDbContext>();
    }

    public RunnerService(RailwayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Run> Run(RunRequest request, CancellationToken cancellationToken = default)
    {
        var run = new Run
        {
            RepositoryId = await GetOrCreateRepository(request.RepositoryPath),
            PipelineFileName = request.PipelineFileName,
            PipelineName = request.Pipeline.Name,
            StartedAt = DateTimeOffset.UtcNow,
            Status = RunStatus.Running
        };

        _dbContext.Runs.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var jobOrder = TopologicalSort(request.Pipeline.Jobs);

        var env = BuildEnvironment(request.Pipeline);
        var overallExitCode = 0;
        var runFailed = false;

        foreach (var jobName in jobOrder)
        {
            if (runFailed)
            {
                break;
            }

            var job = request.Pipeline.Jobs[jobName];
            var runJob = new RunJob
            {
                RunId = run.Id,
                Name = jobName,
                StartedAt = DateTimeOffset.UtcNow,
                Status = RunStatus.Running
            };

            _dbContext.RunJobs.Add(runJob);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var jobEnv = MergeEnvironments(env, job.Env);
            var jobExitCode = 0;
            var jobFailed = false;

            foreach (var step in job.Steps)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    runJob.Status = RunStatus.Cancelled;
                    runJob.FinishedAt = DateTimeOffset.UtcNow;
                    runJob.ExitCode = -1;
                    run.Status = RunStatus.Cancelled;
                    run.FinishedAt = DateTimeOffset.UtcNow;
                    run.ExitCode = -1;
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    return run;
                }

                var stepEnv = MergeEnvironments(jobEnv, step.Env);
                var (exitCode, output) = await ExecuteStep(step, request.RepositoryPath, stepEnv, request.Verbose, cancellationToken);

                var stepStartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-output.Split('\n').Length * 10); // approximate start
                var runStep = new RunStep
                {
                    RunJobId = runJob.Id,
                    Name = step.Name ?? "Unnamed step",
                    Command = step.Run ?? string.Empty,
                    StartedAt = stepStartedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Status = exitCode == 0 ? RunStatus.Succeeded : RunStatus.Failed,
                    ExitCode = exitCode,
                    Output = output
                };

                _dbContext.RunSteps.Add(runStep);

                if (exitCode != 0)
                {
                    jobFailed = true;
                    jobExitCode = exitCode;
                    break;
                }
            }

            runJob.FinishedAt = DateTimeOffset.UtcNow;
            runJob.ExitCode = jobExitCode;
            runJob.Status = jobFailed ? RunStatus.Failed : RunStatus.Succeeded;

            if (jobFailed)
            {
                runFailed = true;
                overallExitCode = jobExitCode;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        run.ExitCode = overallExitCode;
        run.Status = runFailed ? RunStatus.Failed : RunStatus.Succeeded;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return run;
    }

    private static List<string> TopologicalSort(Dictionary<string, Job> jobs)
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();

        void Visit(string jobName)
        {
            if (visiting.Contains(jobName))
            {
                throw new InvalidOperationException($"Circular dependency detected involving job '{jobName}'");
            }

            if (visited.Contains(jobName))
            {
                return;
            }

            visiting.Add(jobName);

            if (jobs.TryGetValue(jobName, out var job))
            {
                foreach (var dependency in job.DependsOn)
                {
                    Visit(dependency);
                }
            }

            visiting.Remove(jobName);
            visited.Add(jobName);
            result.Add(jobName);
        }

        foreach (var jobName in jobs.Keys)
        {
            Visit(jobName);
        }

        return result;
    }

    private static Dictionary<string, string> BuildEnvironment(Pipeline pipeline)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry kvp in Environment.GetEnvironmentVariables())
        {
            if (kvp.Key is string key && kvp.Value is string value)
            {
                env[key] = value;
            }
        }

        foreach (var kvp in pipeline.Variables)
        {
            env[kvp.Key] = kvp.Value;
        }

        return env;
    }

    private static Dictionary<string, string> MergeEnvironments(
        Dictionary<string, string> baseEnv,
        Dictionary<string, string> overrideEnv)
    {
        var result = new Dictionary<string, string>(baseEnv, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in overrideEnv)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }

    private static async Task<(int ExitCode, string Output)> ExecuteStep(
        Step step,
        string workingDirectory,
        Dictionary<string, string> env,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var stepName = step.Name ?? "Unnamed step";
        var command = step.Run ?? string.Empty;

        // Per-step banner using Spectre.Console
        var bannerWidth = stepName.Length + 10;
        var topBanner = "┌─ Step: " + stepName + " " + new string('─', Math.Max(0, bannerWidth - stepName.Length - 9)) + "┐";
        var bottomBanner = "└" + new string('─', bannerWidth) + "┘";
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(topBanner)}[/]");
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]$ {Markup.Escape(command)}[/]");
        }
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(bottomBanner)}[/]");

        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            Arguments = OperatingSystem.IsWindows() ? $"/c \"{command}\"" : $"-c \"{command}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var kvp in env)
        {
            processStartInfo.Environment[kvp.Key] = kvp.Value;
        }

        using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };

        var outputBuilder = new System.Text.StringBuilder();
        var outputTcs = new TaskCompletionSource<bool>();
        var errorTcs = new TaskCompletionSource<bool>();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                // Live stream to console
                AnsiConsole.MarkupLine(e.Data.EscapeMarkup());
            }
            else
            {
                outputTcs.TrySetResult(true);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                // Live stream stderr to console
                AnsiConsole.MarkupLine($"[red]{e.Data.EscapeMarkup()}[/]");
            }
            else
            {
                errorTcs.TrySetResult(true);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTcs.Task, errorTcs.Task);

        return (process.ExitCode, outputBuilder.ToString());
    }

    private async Task<int> GetOrCreateRepository(string repositoryPath)
    {
        var normalizedPath = Path.GetFullPath(repositoryPath);

        var existing = await _dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Path == normalizedPath);

        if (existing != null)
        {
            return existing.Id;
        }

        var repository = new Repository { Path = normalizedPath };
        _dbContext.Repositories.Add(repository);
        await _dbContext.SaveChangesAsync();

        return repository.Id;
    }
}
