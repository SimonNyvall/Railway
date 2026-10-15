using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class RunSettings : CommandSettings
{
    [CommandArgument(0, "<FILE_NAME>")]
    public required string FileName { get; set; }

    [CommandOption("--verbose")]
    public bool Verbose { get; set; }
}

public class RunCommand : AsyncCommand<RunSettings>
{
    private readonly IRunHandler _runHandler;

    public RunCommand()
    {
        _runHandler = LifetimeService.Get<IRunHandler>();
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        RunSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _runHandler.Handle(settings.FileName, settings.Verbose);
            return 0;
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
