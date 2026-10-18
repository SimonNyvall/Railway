using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

/// <summary>
/// <c>railway hooks sync</c>: re-materializes the git hooks from the pipeline YAML and reports the diff.
/// </summary>
public class HooksSyncCommand : AsyncCommand<HooksSettings>
{
    private readonly IHooksSyncHandler _handler;

    public HooksSyncCommand(IHooksSyncHandler handler)
    {
        _handler = handler;
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context,
        HooksSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var succeeded = await _handler.Handle(settings.Path, settings.Verbose);
            return succeeded ? 0 : 1;
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
