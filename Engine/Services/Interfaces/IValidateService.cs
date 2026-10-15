using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

public interface IValidateService
{
	Task<IEnumerable<string>> IsValid(Pipeline pipeline);
}