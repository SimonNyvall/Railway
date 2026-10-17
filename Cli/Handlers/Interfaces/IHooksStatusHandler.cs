namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the result of <c>railway hooks status</c>.
/// </summary>
public interface IHooksStatusHandler
{
    /// <summary>
    /// Renders per-pipeline trigger state, git hook state, lock state and the last recorded run.
    /// </summary>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="verbose">When true, renders full hook/backup paths.</param>
    Task Handle(string? path, bool verbose);
}
