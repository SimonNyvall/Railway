using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Cli.Commands;

public class ValidationSettings : CommandSettings
{
    [CommandArgument(0, "<NAME>")]
    public required string Name { get; set; }
}

public class ValidateCommand : AsyncCommand<ValidationSettings>
{
    private readonly IValidationHandler _validationHandler;

    public ValidateCommand(IValidationHandler validationHandler)
    {
        _validationHandler = validationHandler;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        ValidationSettings settings,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var isValid = await _validationHandler.Handle(settings.Name);
            return isValid ? 0 : 1;
        }
        catch (NotARailwayRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}