using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class ListCommand : AsyncCommand
{
    private readonly IListHandler _listHandler;

    public ListCommand(IListHandler listHandler)
    {
        _listHandler = listHandler;
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
