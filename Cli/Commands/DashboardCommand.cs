using Spectre.Console.Cli;

namespace Cli.Commands;

public class DashboardCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
