using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class ListCommand : AsyncCommand
{
    private readonly IListHandler _listHandler;

    public ListCommand()
    {
        _listHandler = LifetimeService.Get<IListHandler>();
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _listHandler.Handle();
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
