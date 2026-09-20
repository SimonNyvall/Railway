using System.ComponentModel;

using Cli.Handlers.Interfaces;
using Cli.Services;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class LinkCommand : AsyncCommand
{
	private readonly ILinkHandler _linkHandler;

    public LinkCommand()
    {
        _linkHandler = LifetimeService.Get<ILinkHandler>();
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
		try
		{
			await _linkHandler.Handle();
			return 0;
		}
		catch
		{
			return 1;	
		}
    }
}
