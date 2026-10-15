using Cli.Domain;
using Cli.Handlers.Interfaces;
using Cli.Services;
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

    public ValidateCommand()
    {
        _validationHandler = LifetimeService.Get<IValidationHandler>();
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