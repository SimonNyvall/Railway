using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks install</c>: materializes the git hooks declared by the repository's pipelines.
/// Exits non-zero when the repository declares no git-triggering pipeline.
/// </summary>
public class HooksInstallCommand : AsyncCommand<HooksSettings>
{
    private readonly IHooksInstallHandler _handler;

    public HooksInstallCommand(IHooksInstallHandler handler)
    {
        _handler = handler;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var installed = await _handler.Handle(settings.Path, settings.Verbose);
            return installed ? 0 : 1;
        }
        catch (NotARailwayRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            if (settings.Verbose)
            {
                AnsiConsole.WriteException(ex);
            }
            else
            {
                AnsiConsole.WriteLine($"Error: {ex.Message}");
            }

            return 1;
        }
    }
}
