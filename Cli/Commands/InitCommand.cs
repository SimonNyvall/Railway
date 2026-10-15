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
			var initialized = await _initHandler.Handle();
			return initialized ? 0 : 1;
		}
		catch
		{
			return 1;
		}
    }
}