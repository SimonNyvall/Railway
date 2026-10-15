using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class RunHandler : IRunHandler
{
    private readonly IRunnerService _runnerService;
    private readonly IPipelineDiscoveryService _pipelineDiscoveryService;
    private readonly IValidateService _validateService;

    public RunHandler()
        : this(LifetimeService.Get<IRunnerService>(), LifetimeService.Get<IPipelineDiscoveryService>(), LifetimeService.Get<IValidateService>())
    {
    }

    public RunHandler(IRunnerService runnerService, IPipelineDiscoveryService pipelineDiscoveryService, IValidateService validateService)
    {
        _runnerService = runnerService;
        _pipelineDiscoveryService = pipelineDiscoveryService;
        _validateService = validateService;
    }

    public async Task Handle(string fileName, bool verbose)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow();
        var repoPath = RepositoryHelper.GetRootRepositoryPath()
            ?? throw new NotARailwayRepositoryException();

        var pipelines = await _pipelineDiscoveryService.Discover(railwayPath);

        // Match by file name only (last component of path)
        var matchingPipelines = pipelines.Where(p => Path.GetFileName(p.FileName) == fileName).ToList();

        if (matchingPipelines.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Pipeline file not found:[/] {fileName}");
            AnsiConsole.MarkupLine($"[dim]Hint: run 'railway list' to see available pipelines[/]");
            throw new FileNotFoundException($"Pipeline file not found: {fileName}");
        }

        if (matchingPipelines.Count > 1)
        {
            AnsiConsole.MarkupLine($"[red]Multiple pipeline files match:[/] {fileName}");
            foreach (var p in matchingPipelines)
            {
                AnsiConsole.MarkupLine($"  [dim]{p.Path}[/]");
            }
            throw new InvalidOperationException($"Ambiguous pipeline file name: {fileName}");
        }

        var pipelineInfo = matchingPipelines[0];

        // Parse pipeline with PipelineHelper.TryParse
        var (pipeline, parseError) = await PipelineHelper.TryParse(pipelineInfo.Path);
        if (parseError != null || pipeline == null)
        {
            AnsiConsole.MarkupLine($"[red]Failed to parse pipeline:[/] {fileName}");
            AnsiConsole.MarkupLine($"  [red]✗[/] {parseError}");
            throw new InvalidOperationException($"Pipeline parse failed: {parseError}");
        }

        // Validate with ValidateService
        var validationErrors = await _validateService.IsValid(pipeline);
        if (validationErrors.Any())
        {
            AnsiConsole.MarkupLine($"[red]Pipeline is invalid:[/] {fileName}");
            foreach (var error in validationErrors)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] {error}");
            }
            throw new InvalidOperationException("Pipeline validation failed");
        }

        var request = new RunRequest
        {
            RepositoryPath = repoPath,
            PipelineFileName = fileName,
            Pipeline = pipeline,
            Verbose = verbose
        };

        var run = await _runnerService.Run(request);

        if (run.Status == RunStatus.Succeeded)
        {
            AnsiConsole.MarkupLine("[green]✓ Pipeline completed successfully[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Pipeline failed with exit code {run.ExitCode}[/]");
            throw new InvalidOperationException($"Pipeline failed with exit code {run.ExitCode}");
        }
    }
}
