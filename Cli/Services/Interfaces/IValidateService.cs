using Cli.Domain;

namespace Cli.Services.Interfaces;

public interface IValidateService
{
	Task<IEnumerable<string>> IsValid(Pipeline pipeline);
}