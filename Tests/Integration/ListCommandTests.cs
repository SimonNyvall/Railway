using Cli.Commands;
using Cli.Handlers;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

[Collection("Integration Tests")]
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
    }

    [Fact]
    public async Task List_NoPipelines_ShowsEmptyMessage()
    {
        SetRepoDirectory();
        var console = TestConsoleFactory.Create();
        var registrar = new TestTypeRegistrar(console);
        var app = new CommandApp(registrar);
        app.Configure(c =>
        {
            c.AddCommand<InitCommand>("init");
            c.AddCommand<ListCommand>("list");
        });

        var result = await app.RunAsync(["list"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Contains("No pipelines found", console.Output);
    }
}
