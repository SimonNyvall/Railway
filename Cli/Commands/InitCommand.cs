using Cli.Handlers.Interfaces;
using Cli.Services;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class InitCommand : AsyncCommand
{
	private readonly IInitHandler _initHandler;

    public InitCommand()
    {
        _initHandler = LifetimeService.Get<IInitHandler>();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
		try
		{
			await _initHandler.Handle();
			return 0;
		}
		catch
		{
			return 1;
		}
    }
}