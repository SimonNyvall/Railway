namespace Cli.Handlers.Interfaces;

public interface IValidationHandler
{
	Task<bool> Handle(string name);
}
