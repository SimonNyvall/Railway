using ModelContextProtocol;
using Railway.Mcp.Tools;
using Tests.Infrastructure;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RailwayPathResolver"/>. The two-argument
/// <see cref="RailwayPathResolver.Resolve(string?, string?)"/> overload is used so the
/// environment fallback is deterministic and never mutates the process environment.
/// <para>
/// Serialized with the <c>Integration Tests</c> collection because the current-directory
/// fallback reads the process-wide working directory, which integration tests mutate.
/// </para>
/// </summary>
[Collection("Integration Tests")]
public class RailwayPathResolverTests
{
    [Fact]
    public void Resolve_ExplicitPath_WinsOverEnvironment()
    {
        using var explicitDirectory = new TempDirectory();
        using var environmentDirectory = new TempDirectory();

        var resolved = RailwayPathResolver.Resolve(explicitDirectory.Path, environmentDirectory.Path);

        Assert.Equal(explicitDirectory.Path, resolved);
    }

    [Fact]
    public void Resolve_NullPath_UsesEnvironmentValue()
    {
        using var environmentDirectory = new TempDirectory();

        var resolved = RailwayPathResolver.Resolve(null, environmentDirectory.Path);

        Assert.Equal(environmentDirectory.Path, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankPath_UsesEnvironmentValue(string blankPath)
    {
        using var environmentDirectory = new TempDirectory();

        var resolved = RailwayPathResolver.Resolve(blankPath, environmentDirectory.Path);

        Assert.Equal(environmentDirectory.Path, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankEnvironmentValue_TreatedAsAbsent_FallsBackToCurrentDirectory(string blankEnvironment)
    {
        var expected = Path.GetFullPath(Directory.GetCurrentDirectory());

        var resolved = RailwayPathResolver.Resolve(null, blankEnvironment);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_BothBlank_FallsBackToCurrentDirectory()
    {
        var expected = Path.GetFullPath(Directory.GetCurrentDirectory());

        var resolved = RailwayPathResolver.Resolve("   ", null);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Resolve_NonexistentPath_ThrowsMcpException()
    {
        var missing = Path.Combine(Path.GetTempPath(), "railway-missing-" + Guid.NewGuid().ToString("N"));

        var exception = Assert.Throws<McpException>(() => RailwayPathResolver.Resolve(missing, null));

        Assert.Contains(Path.GetFullPath(missing), exception.Message);
    }

    [Fact]
    public void Resolve_PathIsAFile_ThrowsMcpException()
    {
        using var directory = new TempDirectory();
        var file = directory.File("not-a-directory.txt");
        File.WriteAllText(file, "content");

        var exception = Assert.Throws<McpException>(() => RailwayPathResolver.Resolve(file, null));

        Assert.Contains(Path.GetFullPath(file), exception.Message);
    }

    [Fact]
    public void Resolve_RelativePath_IsMadeAbsolute()
    {
        var expected = Path.GetFullPath(Directory.GetCurrentDirectory());

        var resolved = RailwayPathResolver.Resolve(".", null);

        Assert.Equal(expected, resolved);
    }
}
