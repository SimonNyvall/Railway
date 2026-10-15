using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class DashboardCommand : AsyncCommand
{
    private readonly IDashboardHandler _dashboardHandler;

    public DashboardCommand()
    {
        _dashboardHandler = LifetimeService.Get<IDashboardHandler>();
    }

    protected override async Task<int> ExecuteAsync(
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
