using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class ListHandler : IListHandler
{
    private readonly IPipelineDiscoveryService _pipelineDiscoveryService;

    public ListHandler() : this(LifetimeService.Get<IPipelineDiscoveryService>())
    {
    }

    public ListHandler(IPipelineDiscoveryService pipelineDiscoveryService)
    {
        _pipelineDiscoveryService = pipelineDiscoveryService;
    }

    public async Task Handle()
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow();
        var pipelines = await _pipelineDiscoveryService.Discover(railwayPath);

        if (pipelines.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No pipelines found[/]");
            return;
        }

        foreach (var pipeline in pipelines)
        {
            var name = pipeline.Name ?? "<unknown>";
            var prefix = pipeline.IsValid ? "" : "[red]✗[/] ";
            
            AnsiConsole.MarkupLine($"{prefix}{pipeline.FileName} - name: {name}");
        }
    }
}
