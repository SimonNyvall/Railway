using Cli.Commands;
using Railway.Engine.Domain;
using Railway.Engine.Orchestration.Interfaces;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Integration;

/// <summary>
/// Integration tests for <see cref="ConvertCommand"/>: they drive the real Spectre command pipeline against a temp git
/// repository with a temp SQLite database, exercising the real converter and validator. The validation-failure path
/// substitutes a stubbed <see cref="IPipelineConverter"/> because the real conversion service can never emit a pipeline
/// the validator rejects (it drops missing dependencies and skips step-less jobs); see
/// <see cref="ConvertValidationFailure_KeepsFileAndExitsOne"/>.
/// </summary>
[Collection("Integration Tests")]
public class ConvertCommandTests : IDisposable
{
    private const string GitHubWorkflow = """
        name: CI
        on: [push]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - name: Build
                run: dotnet build
          test:
            needs: build
            runs-on: ubuntu-latest
            steps:
              - name: Test
                run: dotnet test
        """;

    private readonly TestRailwayRepository _repo;
    private readonly string _originalDir;

    public ConvertCommandTests()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _repo = new TestRailwayRepository();
        Directory.SetCurrentDirectory(_repo.Path);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _repo.Dispose();
    }

    private void WriteSource(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repo.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static async Task<(int ExitCode, string Output)> RunConvertAsync(
        TestTypeRegistrar registrar,
        TestConsole console,
        params string[] args)
    {
        var app = new CommandApp(registrar);
        app.Configure(config => config.AddCommand<ConvertCommand>("convert"));

        var exitCode = await app.RunAsync(["convert", .. args], TestContext.Current.CancellationToken);
        return (exitCode, console.Output);
    }

    private static TestTypeRegistrar CreateRegistrar(out TestConsole console)
    {
        console = TestConsoleFactory.Create();
        return new TestTypeRegistrar(console);
    }

    [Fact]
    public async Task Convert_GitHubWorkflow_WritesFileRendersWarningsAndExitsZero()
    {
        var source = Path.Combine(".github", "workflows", "ci.yml");
        WriteSource(source, GitHubWorkflow);

        var registrar = CreateRegistrar(out var console);
        var (exitCode, output) = await RunConvertAsync(registrar, console, source);

        Assert.Equal(0, exitCode);
        Assert.Contains("Converted + validated", output);
        Assert.Contains("Warnings:", output);
        Assert.Contains("cannot be mapped", output);
        Assert.Contains("build", output);
        Assert.Contains("dotnet build", output);

        var outputPath = _repo.GetPipelinePath("ci.yaml");
        Assert.True(File.Exists(outputPath), $"Expected converted pipeline at {outputPath}");
    }

    [Fact]
    public async Task Convert_ExistingTargetWithoutForce_RefusesAndExitsOne()
    {
        var source = Path.Combine(".github", "workflows", "ci.yml");
        WriteSource(source, GitHubWorkflow);

        var firstRegistrar = CreateRegistrar(out var firstConsole);
        var (firstExitCode, _) = await RunConvertAsync(firstRegistrar, firstConsole, source);
        Assert.Equal(0, firstExitCode);
        var original = await File.ReadAllTextAsync(
            _repo.GetPipelinePath("ci.yaml"), TestContext.Current.CancellationToken);

        var registrar = CreateRegistrar(out var console);
        var (exitCode, output) = await RunConvertAsync(registrar, console, source);

        Assert.Equal(1, exitCode);
        Assert.Contains("already exists", output);
        Assert.Contains("--force", output);
        Assert.Equal(
            original,
            await File.ReadAllTextAsync(_repo.GetPipelinePath("ci.yaml"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Convert_ExistingTargetWithForce_OverwritesAndExitsZero()
    {
        var source = Path.Combine(".github", "workflows", "ci.yml");
        WriteSource(source, GitHubWorkflow);

        var firstRegistrar = CreateRegistrar(out var firstConsole);
        await RunConvertAsync(firstRegistrar, firstConsole, source);
        await File.WriteAllTextAsync(
            _repo.GetPipelinePath("ci.yaml"), "name: stale\njobs: {}\n", TestContext.Current.CancellationToken);

        var registrar = CreateRegistrar(out var console);
        var (exitCode, output) = await RunConvertAsync(registrar, console, source, "--force");

        Assert.Equal(0, exitCode);
        Assert.Contains("Converted + validated", output);

        var content = await File.ReadAllTextAsync(
            _repo.GetPipelinePath("ci.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("name: CI", content);
        Assert.DoesNotContain("stale", content);
    }

    [Fact]
    public async Task Convert_UndetectableFormatWithoutFormatOption_ExitsOneWithMessage()
    {
        WriteSource("mystery.yaml", "foo: bar\n");

        var registrar = CreateRegistrar(out var console);
        var (exitCode, output) = await RunConvertAsync(registrar, console, "mystery.yaml");

        Assert.Equal(1, exitCode);
        Assert.Contains("Cannot detect pipeline format", output);
        Assert.Contains("--format", output);
        Assert.False(File.Exists(_repo.GetPipelinePath("mystery.yaml")));
    }

    [Fact]
    public async Task Convert_MissingRailwayFolder_ExitsOneWithInitHint()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".git"));
        File.WriteAllText(Path.Combine(directory.Path, "workflow.yml"), GitHubWorkflow);

        Directory.SetCurrentDirectory(directory.Path);
        var registrar = CreateRegistrar(out var console);
        var (exitCode, output) = await RunConvertAsync(registrar, console, "workflow.yml");
        Directory.SetCurrentDirectory(_repo.Path);

        Assert.Equal(1, exitCode);
        Assert.Contains("railway init", output);
    }

    [Fact]
    public async Task ConvertValidationFailure_KeepsFileAndExitsOne()
    {
        // The real conversion service maps best-effort and can never produce a pipeline the validator rejects, so the
        // seam is stubbed: the fake writes a file and reports validation errors, exactly the shape the engine returns
        // when the written pipeline fails validation. Under TestConsole the handler is non-interactive, so it keeps the
        // file instead of prompting.
        var outputPath = _repo.GetPipelinePath("ci.yaml");
        WriteSource("workflow.yml", GitHubWorkflow);

        var registrar = CreateRegistrar(out var console);
        registrar.RegisterInstance(typeof(IPipelineConverter), new ValidationFailingConverter(outputPath));

        var (exitCode, output) = await RunConvertAsync(registrar, console, "workflow.yml");

        Assert.Equal(1, exitCode);
        Assert.Contains("Converted pipeline failed validation", output);
        Assert.Contains("Job 'build' has no steps", output);
        Assert.Contains("Kept", output);
        Assert.True(File.Exists(outputPath));
    }

    /// <summary>
    /// Minimal <see cref="IPipelineConverter"/> stub that writes a valid-looking pipeline file but reports validation
    /// errors, standing in for a real conversion whose output the validator rejects.
    /// </summary>
    private sealed class ValidationFailingConverter : IPipelineConverter
    {
        private readonly string _outputPath;

        public ValidationFailingConverter(string outputPath)
        {
            _outputPath = outputPath;
        }

        /// <inheritdoc/>
        public Task<ConversionResult> ConvertAsync(
            string sourcePath,
            SourceFormat? formatHint,
            string cwd,
            string? outputName,
            bool force,
            CancellationToken ct = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
            File.WriteAllText(_outputPath, "name: CI\njobs:\n  build:\n    steps:\n      - run: dotnet build\n");

            return Task.FromResult(new ConversionResult(
                ConversionStatus.Succeeded,
                SourceFormat.GitHub,
                _outputPath,
                null,
                ["job 'build': 'runs-on' cannot be mapped"],
                ["Job 'build' has no steps"]));
        }
    }
}
