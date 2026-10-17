using System.Runtime.InteropServices;

using Railway.Engine.Domain;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Serializes the notification tests (they mutate process-wide environment variables) and keeps them
/// from racing the parallelized unit suite or the serialized integration collection.
/// </summary>
[CollectionDefinition("Notification Service Tests", DisableParallelization = true)]
public class NotificationServiceTestCollection
{
}

[Collection("Notification Service Tests")]
public sealed class NotificationServiceTests
{
    [Fact]
    public async Task NotifyAsync_MacOS_InvokesOsascriptWithDisplayNotification()
    {
        using var env = MacOS();

        var runner = new FakeSystemCommandRunner();
        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync(
            "Railway: build",
            "Pipeline 'build' failed",
            NotificationLevel.OnFailure);

        var call = Assert.Single(runner.RunCalls);
        Assert.Equal("osascript", call.FileName);
        Assert.Equal(
            ["-e", "display notification \"Pipeline 'build' failed\" with title \"Railway: build\""],
            call.Arguments);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task NotifyAsync_MacOS_EscapesQuotesAndBackslashesInTitleAndMessage()
    {
        using var env = MacOS();

        var runner = new FakeSystemCommandRunner();

        await CreateService(runner, []).NotifyAsync(
            "a \"b\"",
            "c\\d \"e\"",
            NotificationLevel.Always);

        var call = Assert.Single(runner.RunCalls);
        Assert.Equal("osascript", call.FileName);

        // AppleScript literals: \ -> \\ and " -> \" for both the message and the title.
        var escapedTitle = "a \\\"b\\\"";
        var escapedMessage = "c\\\\d \\\"e\\\"";
        var expected = "display notification \"" + escapedMessage + "\" with title \"" + escapedTitle + "\"";

        Assert.Equal(["-e", expected], call.Arguments);
    }

    [Fact]
    public async Task NotifyAsync_Linux_InvokesNotifySendWithVerbatimArguments()
    {
        using var env = Linux();

        var runner = new FakeSystemCommandRunner();

        await CreateService(runner, []).NotifyAsync("a \"t\"", "c\\d", NotificationLevel.Always);

        var call = Assert.Single(runner.RunCalls);
        Assert.Equal("notify-send", call.FileName);

        // No shell is involved, so nothing is escaped: the values are passed verbatim.
        Assert.Equal(["a \"t\"", "c\\d"], call.Arguments);
    }

    [Fact]
    public async Task NotifyAsync_Windows_InvokesPowerShellToast()
    {
        using var env = Windows();

        var runner = new FakeSystemCommandRunner();

        await CreateService(runner, []).NotifyAsync("Railway build", "Pipeline failed", NotificationLevel.Always);

        var call = Assert.Single(runner.RunCalls);
        Assert.Equal("powershell", call.FileName);
        Assert.Equal("-NoProfile", call.Arguments[0]);
        Assert.Equal("-NonInteractive", call.Arguments[1]);
        Assert.Equal("-Command", call.Arguments[2]);

        var script = call.Arguments[3];
        Assert.Contains("ToastNotificationManager", script, StringComparison.Ordinal);
        Assert.Contains("'Railway build'", script, StringComparison.Ordinal);
        Assert.Contains("'Pipeline failed'", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyAsync_Windows_EscapesSingleQuotesInTitleAndMessage()
    {
        using var env = Windows();

        var runner = new FakeSystemCommandRunner();

        await CreateService(runner, []).NotifyAsync("It's done", "don't panic", NotificationLevel.Always);

        var script = Assert.Single(runner.RunCalls).Arguments[3];
        Assert.Contains("'It''s done'", script, StringComparison.Ordinal);
        Assert.Contains("'don''t panic'", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotifyAsync_MissingExecutable_DegradesWithoutThrowing()
    {
        using var env = MacOS();

        var runner = new FakeSystemCommandRunner
        {
            RunHandler = (fileName, _, _) => SystemCommandResult.NotFound(fileName),
        };

        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Single(runner.RunCalls);
        Assert.Contains(warnings, warning => warning.Contains("was not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NotifyAsync_NonZeroExitCode_DegradesWithoutThrowing()
    {
        using var env = Linux();

        var runner = new FakeSystemCommandRunner
        {
            RunHandler = (_, _, _) => new SystemCommandResult(1, string.Empty, "no display"),
        };

        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Contains(warnings, warning => warning.Contains("exited with code 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyAsync_RunnerThrows_DegradesWithoutThrowing()
    {
        using var env = MacOS();

        var runner = new FakeSystemCommandRunner
        {
            RunHandler = (_, _, _) => throw new InvalidOperationException("kaboom"),
        };

        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Contains(warnings, warning => warning.Contains("kaboom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotifyAsync_UnknownPlatformOverride_DegradesWithWarning()
    {
        using var env = new EnvironmentScope()
            .Unset(NotificationPolicy.KillSwitchEnvironmentVariable)
            .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, "plan9");

        var runner = new FakeSystemCommandRunner();
        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Empty(runner.RunCalls);
        Assert.Contains(warnings, warning => warning.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NotifyAsync_KillSwitchSuppressesNotification()
    {
        using var env = new EnvironmentScope()
            .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, "macos")
            .Set(NotificationPolicy.KillSwitchEnvironmentVariable, "0");

        var runner = new FakeSystemCommandRunner();
        var warnings = new List<string>();

        await CreateService(runner, warnings).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Empty(runner.RunCalls);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task NotifyAsync_KillSwitchUnset_DoesNotSuppress()
    {
        using var env = MacOS().Unset(NotificationPolicy.KillSwitchEnvironmentVariable);

        var runner = new FakeSystemCommandRunner();

        await CreateService(runner, []).NotifyAsync("t", "m", NotificationLevel.Always);

        Assert.Single(runner.RunCalls);
    }

    [Theory]
    [InlineData(false, NotificationLevel.Never, true, false)]
    [InlineData(false, NotificationLevel.OnFailure, true, false)]
    [InlineData(false, NotificationLevel.Always, true, false)]
    [InlineData(true, NotificationLevel.Never, true, false)]
    [InlineData(true, NotificationLevel.Never, false, false)]
    [InlineData(true, NotificationLevel.OnFailure, true, true)]
    [InlineData(true, NotificationLevel.OnFailure, false, false)]
    [InlineData(true, NotificationLevel.Always, true, true)]
    [InlineData(true, NotificationLevel.Always, false, true)]
    public void ShouldNotify_MatchesSuppressionMatrix(
        bool notify,
        NotificationLevel level,
        bool failed,
        bool expected) =>
        Assert.Equal(expected, NotificationPolicy.ShouldNotify(notify, level, failed));

    [Theory]
    [InlineData("macos", NotificationPlatform.MacOS)]
    [InlineData("MacOS", NotificationPlatform.MacOS)]
    [InlineData("darwin", NotificationPlatform.MacOS)]
    [InlineData("linux", NotificationPlatform.Linux)]
    [InlineData("windows", NotificationPlatform.Windows)]
    [InlineData("win", NotificationPlatform.Windows)]
    public void ResolvePlatform_OverrideSelectsPlatform(string overrideValue, NotificationPlatform expected)
    {
        using var env = new EnvironmentScope()
            .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, overrideValue);

        Assert.Equal(expected, NotificationPolicy.ResolvePlatform());
    }

    [Theory]
    [InlineData("plan9")]
    [InlineData("  ")]
    public void ResolvePlatform_UnknownOrBlankOverride_FallsBackToHostOrNull(string overrideValue)
    {
        using var env = new EnvironmentScope()
            .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, overrideValue);

        // A whitespace-only override falls back to the host OS; a real unknown value yields null.
        var expected = string.IsNullOrWhiteSpace(overrideValue)
            ? HostPlatform()
            : null;

        Assert.Equal(expected, NotificationPolicy.ResolvePlatform());
    }

    [Fact]
    public void ResolvePlatform_DefaultsToHostOperatingSystem()
    {
        using var env = new EnvironmentScope()
            .Unset(NotificationPolicy.PlatformOverrideEnvironmentVariable);

        Assert.Equal(HostPlatform(), NotificationPolicy.ResolvePlatform());
    }

    private static NotificationService CreateService(FakeSystemCommandRunner runner, List<string> warnings) =>
        new(runner, warnings.Add);

    private static EnvironmentScope MacOS() => new EnvironmentScope()
        .Unset(NotificationPolicy.KillSwitchEnvironmentVariable)
        .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, "macos");

    private static EnvironmentScope Linux() => new EnvironmentScope()
        .Unset(NotificationPolicy.KillSwitchEnvironmentVariable)
        .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, "linux");

    private static EnvironmentScope Windows() => new EnvironmentScope()
        .Unset(NotificationPolicy.KillSwitchEnvironmentVariable)
        .Set(NotificationPolicy.PlatformOverrideEnvironmentVariable, "windows");

    private static NotificationPlatform? HostPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return NotificationPlatform.MacOS;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return NotificationPlatform.Linux;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return NotificationPlatform.Windows;
        }

        return null;
    }
}

/// <summary>
/// Sets/unset environment variables for the duration of a test and restores the previous values on
/// dispose, so nothing leaks into other tests.
/// </summary>
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues = [];

    public EnvironmentScope Set(string name, string value)
    {
        Remember(name);
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public EnvironmentScope Unset(string name)
    {
        Remember(name);
        Environment.SetEnvironmentVariable(name, null);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private void Remember(string name)
    {
        if (!_originalValues.ContainsKey(name))
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
        }
    }
}
