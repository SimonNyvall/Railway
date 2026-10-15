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

    public async Task<RunOutcome> RunAsync(
        string startPath,
        string fileName,
        bool verbose,
        IProgress<RunEvent>? progress = null,
        CancellationToken ct = default)
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

        var request = new RunRequest
        {
            RepositoryPath = repoPath,
            PipelineFileName = fileName,
            Pipeline = pipeline,
            Verbose = verbose
        };

        var run = await _runnerService.Run(request, progress, ct);

        return new RunOutcome(run);
    }
}
