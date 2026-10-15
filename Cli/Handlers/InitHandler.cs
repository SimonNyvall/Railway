using Railway.Engine.Domain;
using Cli.Handlers.Interfaces;
using Railway.Engine.Orchestration;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console;

namespace Cli.Handlers;

public class InitHandler : IInitHandler
{
    private readonly IRepositoryInitializer _repositoryInitializer;

    public InitHandler(IRepositoryInitializer repositoryInitializer)
    {
        _repositoryInitializer = repositoryInitializer;
    }

    public async Task<bool> Handle()
    {
        InitResult result;

        try
        {
            result = await _repositoryInitializer.InitializeAsync(Directory.GetCurrentDirectory());
        }
        catch (NotAGitRepositoryException ex)
        {
            AnsiConsole.WriteLine(ex.Message);
            return false;
        }

        var root = result.RootRepositoryPath;

        if (result.Created)
        {
            AnsiConsole.WriteLine(
                $"Initialized empty Railway repository in {Path.Combine(root!, ".git")}"
            );
        }
        else
        {
            AnsiConsole.WriteLine(
                $"Reinitialized existing Railway repository in {Path.Combine(root!, ".git")}"
            );
        }

        return true;
    }
}
