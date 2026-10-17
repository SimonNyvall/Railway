namespace Cli.Handlers.Interfaces;

public interface IRunHandler
{
    /// <summary>
    /// Runs <paramref name="fileName"/> from <paramref name="path"/> (repository root or any path
    /// inside it); when <paramref name="path"/> is null the current working directory is used.
    /// </summary>
    Task Handle(string fileName, bool verbose, string? path = null);
}
