using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class PipelineRunner : IPipelineRunner
{
    private readonly IRunnerService _runnerService;
    private readonly IPipelineDiscoveryService _pipelineDiscoveryService;
    private readonly IValidateService _validateService;

    public PipelineRunner(
        IRunnerService runnerService,
        IPipelineDiscoveryService pipelineDiscoveryService,
        IValidateService validateService)
    {
        _runnerService = runnerService;
        _pipelineDiscoveryService = pipelineDiscoveryService;
        _validateService = validateService;
    }

    /// <summary>
    /// Runs a pipeline. <paramref name="trigger"/> records run provenance
    /// (default <see cref="RunTrigger.Manual"/>); callers that only need a run
    /// can keep omitting it — no call-site changes are required.
    /// </summary>
    /// <remarks>
    /// The <see cref="IPipelineRunner"/> contract is implemented explicitly with its
    /// original five-parameter signature, so existing hosts (CLI, MCP) and their fakes
    /// observe an unchanged method shape; the trigger-carrying overload is available on
    /// the concrete type.
    /// </remarks>
    public async Task<RunOutcome> RunAsync(
        string startPath,
        string fileName,
        bool verbose,
        IProgress<RunEvent>? progress = null,
        CancellationToken ct = default,
        RunTrigger? trigger = null)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(startPath);
        var repoPath = RepositoryHelper.GetRootRepositoryPath(startPath)
            ?? throw new NotARailwayRepositoryException();

        var pipelines = await _pipelineDiscoveryService.Discover(railwayPath);

        // Match by file name only (last component of path)
        var matchingPipelines = pipelines.Where(p => Path.GetFileName(p.FileName) == fileName).ToList();

        if (matchingPipelines.Count == 0)
        {
            throw new PipelineFileNotFoundException(fileName);
        }

        if (matchingPipelines.Count > 1)
        {
            throw new AmbiguousPipelineException(
                fileName,
                matchingPipelines.Select(p => p.Path).ToList());
        }

        var pipelineInfo = matchingPipelines[0];

        // Parse pipeline with PipelineHelper.TryParse
        var (pipeline, parseError) = await PipelineHelper.TryParse(pipelineInfo.Path);
        if (parseError != null || pipeline == null)
        {
            throw new PipelineInvalidException(
                fileName,
                [parseError ?? string.Empty],
                PipelineInvalidKind.Parse);
        }

        // Validate with ValidateService
        var validationErrors = await _validateService.IsValid(pipeline);
        if (validationErrors.Any())
        {
            throw new PipelineInvalidException(
                fileName,
                validationErrors.ToList(),
                PipelineInvalidKind.Validation);
        }

        var resolvedTrigger = trigger ?? RunTrigger.Manual;

        var request = new RunRequest
        {
            RepositoryPath = repoPath,
            PipelineFileName = fileName,
            Pipeline = pipeline,
            Verbose = verbose,
            Trigger = resolvedTrigger.Key,
            TriggerDetail = resolvedTrigger.Detail
        };

        var run = await _runnerService.Run(request, progress, ct);

        return new RunOutcome(run);
    }

    /// <summary>
    /// Original <see cref="IPipelineRunner"/> contract (five parameters) so existing
    /// hosts keep working unchanged; provenance defaults to
    /// <see cref="RunTrigger.Manual"/>. Trigger-aware callers use the public
    /// <c>RunAsync</c> overload on the concrete type.
    /// </summary>
    Task<RunOutcome> IPipelineRunner.RunAsync(
        string startPath,
        string fileName,
        bool verbose,
        IProgress<RunEvent>? progress,
        CancellationToken ct) =>
        RunAsync(startPath, fileName, verbose, progress, ct, trigger: null);
}
