using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Services;
using Cli.Services.Interfaces;

using Spectre.Console;

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
        if (_pipelineNames.Length == 0)
        {
            AnsiConsole.WriteLine("[red] No pipeliens found[/]");
            return;
        }

		// List all pipelines
        var options = new string[_pipelineNames.Length + 1];

        for (int i = 0; i < _pipelineNames.Length; i++)
        {
            options[i] = _pipelineNames[i];
        }

        const string DoneOption = "Done";
        options[^1] = DoneOption;

        var selectedPipelines = new List<string>(_pipelineNames.Length);

		// Make the user select pipelines and add an option for done.
        do
        {
            var choise = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a [green]pipeline[/]:")
                    .AddChoices(options));

            if (choise == DoneOption)
                continue;

            selectedPipelines.Add(choise);

            AnsiConsole.MarkupLine($"Deploying to [blue]{selectedPipelines[^1]}[/]");
        } while (selectedPipelines[^1] != DoneOption);

		// Create the pipeline and add it to ef
        var pipelines = new List<Pipeline>();

        // Get the pipeline path

		//var pipeline = await PipelineHelper.Parse(path);
	}
}