using Cli.Commands;
using Cli.Handlers.Interfaces;
using Railway.Engine.Domain;
using Spectre.Console.Cli;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Verifies that the Spectre <c>--format</c> option on <see cref="ConvertCommand"/> binds the lowercase
/// <c>github|gitlab|azure</c> values (and mixed case) to the <see cref="SourceFormat"/> enumeration members, and that
/// omitting the option yields <see langword="null"/>.
/// </summary>
public class ConvertOptionBindingTests
{
    [Theory]
    [InlineData("github", SourceFormat.GitHub)]
    [InlineData("gitlab", SourceFormat.GitLab)]
    [InlineData("azure", SourceFormat.Azure)]
    [InlineData("GitHub", SourceFormat.GitHub)]
    [InlineData("GITLAB", SourceFormat.GitLab)]
    public async Task Convert_FormatOption_BindsEnumCaseInsensitively(string value, SourceFormat expected)
    {
        var exitCode = await RunConvertAsync("pipeline.yaml", "--format", value);

        Assert.Equal(0, exitCode);
        Assert.True(CapturingConvertHandler.Called);
        Assert.Equal(expected, CapturingConvertHandler.LastFormat);
    }

    [Fact]
    public async Task Convert_OmittingFormat_PassesNull()
    {
        var exitCode = await RunConvertAsync("pipeline.yaml");

        Assert.Equal(0, exitCode);
        Assert.Null(CapturingConvertHandler.LastFormat);
    }

    [Fact]
    public async Task Convert_OutputAndForceOptions_Bind()
    {
        var exitCode = await RunConvertAsync(
            "pipeline.yaml", "--output", "custom.yaml", "--force");

        Assert.Equal(0, exitCode);
        Assert.Equal("custom.yaml", CapturingConvertHandler.LastOutputName);
        Assert.True(CapturingConvertHandler.LastForce);
    }

    private static async Task<int> RunConvertAsync(params string[] args)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"railway_binding_{Guid.NewGuid()}.db");
        CapturingConvertHandler.Reset();

        try
        {
            var console = TestConsoleFactory.Create();
            var registrar = new TestTypeRegistrar(console, databasePath);
            registrar.Register(typeof(IConvertHandler), typeof(CapturingConvertHandler));

            var app = new CommandApp(registrar);
            app.Configure(config => config.AddCommand<ConvertCommand>("convert"));

            return await app.RunAsync(["convert", .. args], TestContext.Current.CancellationToken);
        }
        finally
        {
            TryDelete(databasePath);
        }
    }

    public sealed class CapturingConvertHandler : IConvertHandler
    {
        public static bool Called { get; private set; }

        public static SourceFormat? LastFormat { get; private set; }

        public static string? LastOutputName { get; private set; }

        public static bool LastForce { get; private set; }

        public static void Reset()
        {
            Called = false;
            LastFormat = null;
            LastOutputName = null;
            LastForce = false;
        }

        public Task<bool> Handle(string sourcePath, SourceFormat? format, string? outputName, bool force)
        {
            Called = true;
            LastFormat = format;
            LastOutputName = outputName;
            LastForce = force;
            return Task.FromResult(true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
