using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class ListHandler : IListHandler
{
    private readonly IPipelineLister _pipelineLister;

    public ListHandler(IPipelineLister pipelineLister)
    {
        _pipelineLister = pipelineLister;
    }

    public async Task Handle()
    {
        var pipelines = await _pipelineLister.ListAsync(Directory.GetCurrentDirectory());

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
