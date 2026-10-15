using Cli.Domain;

namespace Cli.Services.Interfaces;

public interface IRunnerService
{
    Task<Run> Run(RunRequest request, CancellationToken cancellationToken = default);
}
