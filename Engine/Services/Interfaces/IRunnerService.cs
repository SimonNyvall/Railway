using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

public interface IRunnerService
{
    Task<Run> Run(RunRequest request, IProgress<RunEvent>? progress = null, CancellationToken cancellationToken = default);
}
