using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks uninstall [--purge]</c>: removes the marker-owned hook artifacts.
/// </summary>
public class HooksUninstallCommand : AsyncCommand<HooksUninstallSettings>
{
    private readonly IHooksUninstallHandler _handler;

    public HooksUninstallCommand(IHooksUninstallHandler handler)
    {
        _handler = handler;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksUninstallSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await _handler.Handle(settings.Path, settings.Purge, settings.Verbose);
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
