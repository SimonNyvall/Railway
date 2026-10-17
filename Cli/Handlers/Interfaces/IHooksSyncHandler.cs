namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the result of <c>railway hooks sync</c>.
/// </summary>
public interface IHooksSyncHandler
{
    /// <summary>
    /// Re-materializes the git hooks from the pipeline YAML and renders the resulting diff.
    /// </summary>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="verbose">When true, renders extra detail.</param>
    /// <returns>
    /// <see langword="true"/> when every schedule operation succeeded (exit 0); <see langword="false"/>
    /// when any schedule operation failed (exit 1).
    /// </returns>
    Task<bool> Handle(string? path, bool verbose);
}
