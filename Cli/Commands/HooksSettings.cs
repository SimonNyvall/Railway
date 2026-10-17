using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// Shared settings for the <c>hooks</c> command branch: the repository path each hook command
/// operates on and the verbose switch. Subcommand settings derive from this type (task 1.6,
/// assumption A3 — inner commands of <c>AddBranch</c> must implement
/// <c>ICommandLimiter&lt;HooksSettings&gt;</c>; the covariant <c>ICommandLimiter&lt;out TSettings&gt;</c>
/// makes the derived types qualify). Spectre only propagates an option defined on the branch when the
/// child settings type matches the branch's, so settings types that add their own options redeclare
/// <c>--path</c>/<c>--verbose</c> (see the remarks on each).
/// </summary>
public class HooksSettings : CommandSettings
{
    /// <summary>Gets or sets the repository root (or any path inside it). Defaults to the current directory.</summary>
    [CommandOption("--path <PATH>")]
    public string? Path { get; set; }

    /// <summary>Gets or sets a value that indicates whether extra detail is rendered. The default is <see langword="false"/>.</summary>
    [CommandOption("--verbose")]
    public bool Verbose { get; set; }
}

/// <summary>Settings for <c>hooks uninstall</c>: the shared options plus <c>--purge</c>.</summary>
public class HooksUninstallSettings : HooksSettings
{
    // Redeclared so Spectre binds the option on this command itself: the branch node already owns
    // `--path`/`--verbose`, and Spectre silently drops an inherited option when the child settings
    // type differs from the parent branch's type (AllowParentOption compares settings types).
    // A shadowing property has a distinct metadata token, so it is treated as a new option.

    /// <inheritdoc/>
    [CommandOption("--path <PATH>")]
    public new string? Path { get; set; }

    /// <inheritdoc/>
    [CommandOption("--verbose")]
    public new bool Verbose { get; set; }

    /// <summary>Gets or sets a value that indicates whether the <c>.railway/logs/</c> directory is also deleted. The default is <see langword="false"/>.</summary>
    [CommandOption("--purge")]
    public bool Purge { get; set; }
}

/// <summary>Settings for <c>hooks run &lt;event&gt;</c>: the shared options plus the event and run flags.</summary>
public class HooksRunSettings : HooksSettings
{
    // See HooksUninstallSettings: redeclared so `hooks run <event> --path …` (the form the generated
    // wrappers use) binds on the run command instead of being dropped as an inherited branch option.

    /// <inheritdoc/>
    [CommandOption("--path <PATH>")]
    public new string? Path { get; set; }

    /// <inheritdoc/>
    [CommandOption("--verbose")]
    public new bool Verbose { get; set; }

    /// <summary>
    /// Gets or sets the hook event keyword to fire (<c>post-commit</c>, <c>push</c>, <c>post-merge</c>,
    /// <c>post-checkout</c>, <c>schedule</c>, <c>watch</c>).
    /// </summary>
    [CommandArgument(0, "<EVENT>")]
    public required string Event { get; set; }

    /// <summary>Gets or sets the pipeline file name that restricts the fan-out. Defaults to every subscribed pipeline.</summary>
    [CommandOption("--pipeline <FILE>")]
    public string? Pipeline { get; set; }

    /// <summary>Gets or sets a value that indicates whether a detached child is spawned per pipeline so the command returns immediately. The default is <see langword="false"/>.</summary>
    [CommandOption("--detach")]
    public bool Detach { get; set; }

    /// <summary>
    /// Gets or sets a value that indicates whether detaching is suppressed. Set on the child a
    /// <c>--detach</c> parent spawns so it always runs in-process (effective detach is
    /// <c>Detach &amp;&amp; !NoDetach</c>).
    /// </summary>
    [CommandOption("--no-detach")]
    public bool NoDetach { get; set; }

    /// <summary>Gets or sets a value that indicates whether notifications are suppressed regardless of pipeline preferences. The default is <see langword="false"/>.</summary>
    [CommandOption("--no-notify")]
    public bool NoNotify { get; set; }

    /// <summary>Gets or sets the cron expression of the schedule entry that fired (recorded as provenance).</summary>
    [CommandOption("--cron <EXPR>")]
    public string? Cron { get; set; }

    /// <summary>Gets or sets the branch name for a push/checkout event (context + push branch filter).</summary>
    [CommandOption("--branch <BRANCH>")]
    public string? Branch { get; set; }

    /// <summary>Gets or sets the commit SHA involved in the event.</summary>
    [CommandOption("--sha <SHA>")]
    public string? Sha { get; set; }

    /// <summary>Gets or sets the tag name for a tag push (context + push tag filter).</summary>
    [CommandOption("--tag <TAG>")]
    public string? Tag { get; set; }

    /// <summary>Gets or sets the remote name for a push event.</summary>
    [CommandOption("--remote <REMOTE>")]
    public string? Remote { get; set; }

    /// <summary>Gets or sets the previous HEAD for a checkout/merge event (all zeros for a clone).</summary>
    [CommandOption("--prev <SHA>")]
    public string? Previous { get; set; }

    /// <summary>Gets or sets the new HEAD for a checkout/merge event.</summary>
    [CommandOption("--new <SHA>")]
    public string? New { get; set; }

    /// <summary>Gets or sets the raw git flag argument for the event (e.g. <c>1</c> for a branch checkout).</summary>
    [CommandOption("--flag <FLAG>")]
    public string? Flag { get; set; }

    /// <summary>Gets or sets the <c>post-merge</c> squash flag (<c>true</c>/<c>false</c> or <c>1</c>/<c>0</c>).</summary>
    [CommandOption("--is-squash <VALUE>")]
    public string? IsSquash { get; set; }
}
