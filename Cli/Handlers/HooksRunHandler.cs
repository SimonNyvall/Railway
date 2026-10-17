using Cli.Handlers.Interfaces;
using Cli.Rendering;
using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

/// <summary>
/// Renders the result of <c>railway hooks run &lt;event&gt;</c>: one line per subscribed pipeline,
/// a distinct line for skipped runs (lock held) and detached spawns, and a summary. Returns whether
/// every run succeeded or was skipped.
/// </summary>
public class HooksRunHandler : IHooksRunHandler
{
    /// <summary>
    /// The accepted event keywords, built from <see cref="HookScriptHelper.GetEventKeyword"/> so the
    /// CLI never re-declares the event⇄keyword map. <see cref="HookScriptHelper"/> exposes only the
    /// event→keyword direction, so the reverse map is derived here (see task report).
    /// </summary>
    private static readonly Dictionary<string, TriggerEvent> EventsByKeyword =
        Enum.GetValues<TriggerEvent>()
            .ToDictionary(HookScriptHelper.GetEventKeyword, triggerEvent => triggerEvent, StringComparer.Ordinal);

    private readonly IHookRunner _hookRunner;

    public HooksRunHandler(IHookRunner hookRunner)
    {
        _hookRunner = hookRunner;
    }

    /// <inheritdoc />
    public async Task<bool> Handle(
        string eventKeyword,
        string? pipeline,
        bool detach,
        bool notify,
        string? path,
        bool verbose,
        HooksRunContextOptions context,
        CancellationToken ct = default)
    {
        if (!EventsByKeyword.TryGetValue(eventKeyword, out var triggerEvent))
        {
            AnsiConsole.MarkupLine($"[red]✗ Unknown hook event:[/] {eventKeyword}");
            AnsiConsole.MarkupLine($"[dim]Valid events: {string.Join(", ", EventsByKeyword.Keys)}[/]");
            return false;
        }

        if (!TryBuildContext(context, out var hookContext, out var contextError))
        {
            AnsiConsole.MarkupLine($"[red]✗ Invalid hook context:[/] {contextError}");
            return false;
        }

        var outcome = await _hookRunner.RunEventAsync(
            path ?? Directory.GetCurrentDirectory(),
            triggerEvent,
            hookContext,
            pipeline,
            detach,
            notify,
            ct);

        if (outcome.Results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]No pipelines subscribed to '{eventKeyword}'.[/]");
            return true;
        }

        foreach (var result in outcome.Results)
        {
            RenderResult(result);
        }

        if (outcome.AnyFailed)
        {
            AnsiConsole.MarkupLine($"[red]✗ One or more pipelines failed on '{eventKeyword}'.[/]");
            return false;
        }

        AnsiConsole.MarkupLine($"[green]✓ '{eventKeyword}' handled for all subscribed pipelines.[/]");
        return true;
    }

    /// <summary>
    /// Turns the raw command-line context flags into a <see cref="HookEventContext"/>, validating that
    /// <c>--is-squash</c> (when supplied) is a boolean (<c>true</c>/<c>false</c> or <c>1</c>/<c>0</c>).
    /// </summary>
    private static bool TryBuildContext(
        HooksRunContextOptions options,
        out HookEventContext context,
        out string? error)
    {
        error = null;
        bool? isSquash = null;

        if (!string.IsNullOrEmpty(options.IsSquash))
        {
            if (!TryParseBool(options.IsSquash, out var parsed))
            {
                context = new HookEventContext();
                error = $"'--is-squash' must be a boolean (true/false or 1/0), but was '{options.IsSquash}'.";
                return false;
            }

            isSquash = parsed;
        }

        context = new HookEventContext(
            Branch: options.Branch,
            Sha: options.Sha,
            Tag: options.Tag,
            Remote: options.Remote,
            IsSquash: isSquash,
            PreviousSha: options.Previous,
            NewSha: options.New,
            Flag: options.Flag,
            Cron: options.Cron);

        return true;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "on":
                result = true;
                return true;
            case "false" or "0" or "no" or "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static void RenderResult(HookRunResult result)
    {
        Cli.Rendering.HookRunResultRenderer.Render(result, includeTimestamp: false, indent: true);
    }
}
