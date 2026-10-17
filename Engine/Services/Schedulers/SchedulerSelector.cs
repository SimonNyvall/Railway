using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services.Schedulers;

/// <summary>
/// Selects the <see cref="IScheduler"/> for the current process: the
/// <see cref="SchedulerSelector.EnvironmentVariable"/> override when set, otherwise the platform default
/// (launchd on macOS, crontab elsewhere on POSIX, schtasks on Windows).
/// </summary>
public interface ISchedulerSelector
{
    /// <summary>Returns the scheduler to use for the current process.</summary>
    /// <returns>The selected scheduler.</returns>
    /// <exception cref="UnknownSchedulerException">The environment override names an unknown scheduler.</exception>
    IScheduler Select();
}

/// <summary>
/// Platform-based default implementation. The <c>RAILWAY_HOOKS_SCHEDULER</c>
/// environment variable (<c>launchd</c>, <c>crontab</c>, <c>schtasks</c>, <c>fake</c>) overrides the
/// platform default so tests never touch the real machine scheduler.
/// </summary>
public sealed class SchedulerSelector : ISchedulerSelector
{
    /// <summary>Environment variable overriding scheduler selection (tests set this to <c>fake</c>).</summary>
    public const string EnvironmentVariable = "RAILWAY_HOOKS_SCHEDULER";

    private readonly HookPaths _paths;
    private readonly ISystemCommandRunner _runner;
    private readonly IHookLauncher _launcher;
    private readonly Func<string, string?> _environment;

    /// <summary>Initializes a new instance of the <see cref="SchedulerSelector"/> class.</summary>
    /// <param name="paths">Resolves repository slugs and hook paths.</param>
    /// <param name="runner">Runs external commands (<c>launchctl</c>, <c>crontab</c>, <c>schtasks</c>).</param>
    /// <param name="launcher">Supplies the command the schtasks task runs.</param>
    /// <param name="environment">Environment reader; defaults to the process environment.</param>
    public SchedulerSelector(
        HookPaths paths,
        ISystemCommandRunner runner,
        IHookLauncher launcher,
        Func<string, string?>? environment = null)
    {
        _paths = paths;
        _runner = runner;
        _launcher = launcher;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc />
    public IScheduler Select()
    {
        var overrideValue = _environment(EnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue.Trim().ToLowerInvariant() switch
            {
                "launchd" => new LaunchdScheduler(_paths, _runner, environment: _environment),
                "crontab" => new CrontabScheduler(_paths, _runner),
                "schtasks" => new SchtasksScheduler(_paths, _runner, _launcher),
                "fake" => new FakeScheduler(_paths, environment: _environment),
                _ => throw new UnknownSchedulerException(overrideValue.Trim()),
            };
        }

        if (OperatingSystem.IsWindows())
        {
            return new SchtasksScheduler(_paths, _runner, _launcher);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new LaunchdScheduler(_paths, _runner, environment: _environment);
        }

        return new CrontabScheduler(_paths, _runner);
    }
}

/// <summary>
/// Thrown when the <c>RAILWAY_HOOKS_SCHEDULER</c> override names a scheduler that does not exist.
/// </summary>
public sealed class UnknownSchedulerException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="UnknownSchedulerException"/> class.</summary>
    /// <param name="value">The unrecognized value.</param>
    public UnknownSchedulerException(string value)
        : base($"Unknown scheduler '{value}'. Set {SchedulerSelector.EnvironmentVariable} to one of: launchd, crontab, schtasks, fake.")
    {
        Value = value;
    }

    /// <summary>Gets the unrecognized value.</summary>
    public string Value { get; }
}
