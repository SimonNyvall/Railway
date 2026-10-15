using Cli.Handlers.Interfaces;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class InitCommand : AsyncCommand
{
	private readonly IInitHandler _initHandler;

    public InitCommand(IInitHandler initHandler)
    {
        _initHandler = initHandler;
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