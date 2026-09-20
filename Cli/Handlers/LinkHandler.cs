using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;

namespace Cli.Handlers;

public class LinkHandler : ILinkHandler
{
	private readonly string[] _pipelineNames = PipelineHelper.GetPipelineNames();
	private readonly IPipelineService _pipelineService;

    public LinkHandler()
    {
        _pipelineService = LifetimeService.Get<IPipelineService>();
    }

    public async Task Handle()
	{
		// List all pipelines
		
		// Make the user select pipelines and add an option for done.
		
		// Create the pipeline and add it to ef
	}
}
