using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class ConvertSettings : CommandSettings
{
    [CommandArgument(0, "<PATH>")]
    public required string SourceFile { get; set; }

    [CommandOption("--format")]
    public SourceFormat? Format { get; set; }

    [CommandOption("--output")]
    public string? Output { get; set; }

    [CommandOption("--force")]
    public bool Force { get; set; }
}

public class ConvertCommand : AsyncCommand<ConvertSettings>
{
    private readonly IConvertHandler _convertHandler;

    public ConvertCommand(IConvertHandler convertHandler)
    {
        _convertHandler = convertHandler;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ConvertSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var converted = await _convertHandler.Handle(
                settings.SourceFile,
                settings.Format,
                settings.Output,
                settings.Force
            );

            return converted ? 0 : 1;
        }
        catch (NotARailwayRepositoryException ex)
        {
            // The message already carries the hint to run 'railway init'.
            AnsiConsole.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
