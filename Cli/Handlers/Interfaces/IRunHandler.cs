namespace Cli.Handlers.Interfaces;

public interface IRunHandler
{
    Task Handle(string fileName, bool verbose);
}
