namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the result of <c>railway hooks install</c>.
/// </summary>
public interface IHooksInstallHandler
{
    /// <summary>
    /// Installs the git hooks declared by the repository's pipelines and renders what was written.
    /// </summary>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="verbose">When true, renders full artifact paths.</param>
    /// <returns><see langword="true"/> when the repository declares at least one git-triggering pipeline.</returns>
    Task<bool> Handle(string? path, bool verbose);
}
