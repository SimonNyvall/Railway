namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the result of <c>railway hooks uninstall</c>.
/// </summary>
public interface IHooksUninstallHandler
{
    /// <summary>
    /// Removes the marker-owned hook artifacts and renders what was removed, restored or left alone.
    /// </summary>
    /// <param name="path">Repository root (or any path inside it); null uses the current directory.</param>
    /// <param name="purge">When true, also deletes the <c>.railway/logs/</c> directory.</param>
    /// <param name="verbose">When true, renders full artifact paths.</param>
    Task Handle(string? path, bool purge, bool verbose);
}
