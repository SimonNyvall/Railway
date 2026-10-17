using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// Settings for <c>hooks watch</c>: the shared hook options plus the pipeline filter, the debounce
/// window and the initial-run switch. <c>--path</c>/<c>--verbose</c> are redeclared for the same
/// reason as the other hook subcommands (Spectre drops an inherited branch option when the child
/// settings type differs from the branch's).
/// </summary>
public class HooksWatchSettings : HooksSettings
{
    /// <inheritdoc/>
    [CommandOption("--path <PATH>")]
    public new string? Path { get; set; }

    /// <inheritdoc/>
    [CommandOption("--verbose")]
    public new bool Verbose { get; set; }

    /// <summary>Gets or sets the repository root (or any path inside it) supplied as a positional argument. Defaults to the current directory.</summary>
    [CommandArgument(0, "[WATCH_PATH]")]
    public string? WatchPath { get; set; }

    /// <summary>Gets or sets the pipeline file name that restricts the watch. Defaults to every watch-triggering pipeline.</summary>
    [CommandOption("--pipeline <FILE>")]
    public string? Pipeline { get; set; }

    /// <summary>Gets or sets the per-pipeline debounce window (e.g. <c>1s</c>, <c>500ms</c>). Defaults to 1 second.</summary>
    [CommandOption("--debounce <DURATION>")]
    public string? Debounce { get; set; }

    /// <summary>Gets or sets a value that indicates whether each matching pipeline runs once immediately on start. The default is <see langword="false"/>.</summary>
    [CommandOption("--initial")]
    public bool Initial { get; set; }
}
