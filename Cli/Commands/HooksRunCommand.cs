using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks run &lt;event&gt;</c>: fires a hook event for the subscribed pipelines headlessly.
/// Exits 0 when everything succeeded or was skipped, 1 when any run failed or the event is unknown.
/// </summary>
public class HooksRunCommand : AsyncCommand<HooksRunSettings>
{
    private readonly IHooksRunHandler _handler;

    public HooksRunCommand(IHooksRunHandler handler)
    {
        _handler = handler;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksRunSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var succeeded = await _handler.Handle(
                settings.Event,
                settings.Pipeline,
                settings.Detach && !settings.NoDetach,
                notify: !settings.NoNotify,
                settings.Path,
                settings.Verbose,
                new HooksRunContextOptions(
                    Branch: settings.Branch,
                    Sha: settings.Sha,
                    Tag: settings.Tag,
                    Remote: settings.Remote,
                    Previous: settings.Previous,
                    New: settings.New,
                    Flag: settings.Flag,
                    IsSquash: settings.IsSquash,
                    Cron: settings.Cron),
                cancellationToken);

            return succeeded ? 0 : 1;
        }
        catch (NotARailwayRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return 1;
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
}
