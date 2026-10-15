using Cli.Commands;
using Cli.Handlers;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

public class ListCommandTests : IDisposable
{
    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public ListCommandTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
    }

    private void SetRepoDirectory()
    {
        Directory.SetCurrentDirectory(_repo.Path);
        // Debug
        Console.WriteLine($"Test working dir: {Directory.GetCurrentDirectory()}");
        Console.WriteLine($"Repo path: {_repo.Path}");
        Console.WriteLine($"Railway exists: {Directory.Exists(Path.Combine(_repo.Path, ".railway"))}");
    }

    [Fact]
    public async Task List_NoPipelines_ShowsEmptyMessage()
    {
        SetRepoDirectory();
        var console = new TestConsole();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ListCommand>("list");
        });

        var result = await app.RunAsync(["list"]);

        Assert.Equal(0, result);
        Assert.Contains("No pipelines found", console.Output);
    }
}
