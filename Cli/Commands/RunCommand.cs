using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class RunSettings : CommandSettings
{
    [CommandArgument(0, "<FILE_NAME>")]
    public required string FileName { get; set; }

    [CommandOption("--path <PATH>")]
    public string? Path { get; set; }

    [CommandOption("--verbose")]
    public bool Verbose { get; set; }
}

public class RunCommand : AsyncCommand<RunSettings>
{
    private readonly IRunHandler _runHandler;

    public RunCommand(IRunHandler runHandler)
    {
        _runHandler = runHandler;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        RunSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _runHandler.Handle(settings.FileName, settings.Verbose, settings.Path);
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
