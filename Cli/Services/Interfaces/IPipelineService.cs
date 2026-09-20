using Cli.Domain;

namespace Cli.Services.Interfaces;

public interface IPipelineService
{
	Task Add(Pipeline pipeline);
	Task Save();
}
