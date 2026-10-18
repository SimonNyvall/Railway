using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks status</c>: reports per-pipeline trigger and hook state.
/// </summary>
public class HooksStatusCommand : AsyncCommand<HooksSettings>
{
    private readonly IHooksStatusHandler _handler;

    public HooksStatusCommand(IHooksStatusHandler handler)
    {
        _handler = handler;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _handler.Handle(settings.Path, settings.Verbose);
            return 0;
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
