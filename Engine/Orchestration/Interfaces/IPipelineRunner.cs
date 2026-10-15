using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

public interface IPipelineRunner
{
    Task<RunOutcome> RunAsync(
        string startPath,
        string fileName,
        bool verbose,
        IProgress<RunEvent>? progress = null,
        CancellationToken ct = default);
}
