using System.Globalization;

using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks watch</c>: watches the repository in the foreground and runs the pipelines whose
/// <c>watch</c> triggers match, until Ctrl+C stops the process cleanly (exit 0). Fatal setup errors
/// (not a railway repository, an unparseable <c>--debounce</c>) exit 1.
/// </summary>
public class HooksWatchCommand : AsyncCommand<HooksWatchSettings>
{
    private readonly IHooksWatchHandler _handler;

    public HooksWatchCommand(IHooksWatchHandler handler)
    {
        _handler = handler;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksWatchSettings settings,
        CancellationToken cancellationToken)
    {
        if (!TryParseDebounce(settings.Debounce, out var debounce, out var error))
        {
            AnsiConsole.MarkupLine($"[red]Invalid --debounce value:[/] {error}");
            return 1;
        }

        try
        {
            var path = settings.Path ?? settings.WatchPath;
            if (settings.Path != null && settings.WatchPath != null && !string.Equals(settings.Path, settings.WatchPath, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine("[red]Conflicting paths:[/] --path and WATCH_PATH specify different paths; use one.");
                return 1;
            }

            await _handler.Handle(
                settings.Pipeline,
                debounce,
                settings.Initial,
                path,
                settings.Verbose,
                cancellationToken);

            return 0;
        }
        catch (NotARailwayRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C (or host shutdown) is a clean stop.
            return 0;
        }
        catch (Exception ex)
        {
            if (settings.Verbose)
            {
                AnsiConsole.WriteException(ex);
            }
            else
            {
                AnsiConsole.WriteLine($"Error: {ex.Message}");
            }

            return 1;
        }
    }

    /// <summary>
    /// Parses a <c>--debounce</c> value: a number with an optional <c>ms</c>/<c>s</c>/<c>m</c>/<c>h</c>
    /// unit (a bare number means seconds). Null/blank means "use the default".
    /// </summary>
    /// <param name="value">Raw option value.</param>
    /// <param name="debounce">Parsed window, or null when <paramref name="value"/> is blank.</param>
    /// <param name="error">Human-readable reason when parsing fails.</param>
    /// <returns>True when the value parsed (or was blank).</returns>
    internal static bool TryParseDebounce(string? value, out TimeSpan? debounce, out string error)
    {
        debounce = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var text = value.Trim();

        double millisecondsPerUnit;

        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
            millisecondsPerUnit = 1;
        }
        else if (text.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
            millisecondsPerUnit = 1_000;
        }
        else if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
            millisecondsPerUnit = 60_000;
        }
        else if (text.EndsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
            millisecondsPerUnit = 3_600_000;
        }
        else
        {
            // A bare number is seconds.
            millisecondsPerUnit = 1_000;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            error = $"'{value}' is not a valid duration (use e.g. 1s, 500ms, 2m)";
            return false;
        }

        try
        {
            debounce = TimeSpan.FromMilliseconds(number * millisecondsPerUnit);
        }
        catch (OverflowException)
        {
            error = $"'{value}' is too large";
            return false;
        }

        return true;
    }
}
