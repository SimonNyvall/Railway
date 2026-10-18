using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class DashboardCommand : AsyncCommand
{
    private readonly IDashboardHandler _dashboardHandler;

    public DashboardCommand(IDashboardHandler dashboardHandler)
    {
        _dashboardHandler = dashboardHandler;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _dashboardHandler.Handle();
            return 0;
        }
        catch (NotARailwayRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
