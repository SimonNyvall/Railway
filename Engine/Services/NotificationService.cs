using System.Runtime.InteropServices;

using Railway.Engine.Domain;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services;

/// <summary>
/// Production <see cref="INotificationService"/>: sends a native desktop notification by running a
/// per-OS one-liner through <see cref="ISystemCommandRunner"/> (macOS <c>osascript</c>, Linux
/// <c>notify-send</c>, Windows PowerShell toast). Degrades gracefully: a missing executable or a
/// failing command is reported as a warning and never thrown, and nothing is ever written to stdout.
/// </summary>
/// <remarks>
/// The <c>RAILWAY_HOOKS_NOTIFY=0</c> global kill switch is read here (see
/// <see cref="NotificationPolicy.IsDisabledByEnvironment"/>), so every caller benefits without
/// having to remember it. The pipeline-level preference (<c>notify: never|on-failure|always</c>) and
/// the <c>--no-notify</c> flag are decided by the caller via
/// <see cref="NotificationPolicy.ShouldNotify"/>.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private readonly ISystemCommandRunner _runner;
    private readonly Action<string> _warn;

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationService"/> class.
    /// </summary>
    /// <param name="runner">The seam used to launch the platform notifier executable.</param>
    /// <param name="warn">
    /// Sink for degradation warnings. Defaults to standard error; hosts and tests can redirect it.
    /// Warnings are never written to stdout.
    /// </param>
    public NotificationService(ISystemCommandRunner runner, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(runner);

        _runner = runner;
        _warn = warn ?? (static message => Console.Error.WriteLine(message));
    }

    /// <inheritdoc />
    public Task NotifyAsync(string title, string message, NotificationLevel level)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        if (NotificationPolicy.IsDisabledByEnvironment())
        {
            return Task.CompletedTask;
        }

        var platform = NotificationPolicy.ResolvePlatform();

        if (platform is null)
        {
            _warn("Railway: desktop notifications are not supported on this platform; skipping notification.");
            return Task.CompletedTask;
        }

        var command = NotificationCommand.For(platform.Value, title, message);

        SystemCommandResult result;

        try
        {
            result = _runner.Run(command.FileName, command.Arguments);
        }
        catch (Exception exception)
        {
            // Graceful degradation: a notification must never fail a run.
            _warn($"Railway: notification via '{command.FileName}' failed: {exception.Message}");
            return Task.CompletedTask;
        }

        if (result.ExitCode == SystemCommandResult.ExecutableNotFoundExitCode)
        {
            _warn($"Railway: notification executable '{command.FileName}' was not found; skipping notification.");
        }
        else if (result.ExitCode != 0)
        {
            _warn($"Railway: notification via '{command.FileName}' exited with code {result.ExitCode}: {result.StandardError}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The operating systems <see cref="NotificationService"/> knows how to notify on.
/// </summary>
public enum NotificationPlatform
{
    /// <summary>Apple macOS (AppleScript via <c>osascript</c>).</summary>
    MacOS,

    /// <summary>Linux and other freedesktop systems (<c>notify-send</c>).</summary>
    Linux,

    /// <summary>Microsoft Windows (PowerShell toast).</summary>
    Windows,
}

/// <summary>
/// The pure rules that decide whether a hook-run notification is sent, plus the environment seams
/// that make platform selection and the global kill switch testable without touching a real machine.
/// </summary>
public static class NotificationPolicy
{
    /// <summary>Environment variable that disables notifications globally when set to <c>0</c>.</summary>
    public const string KillSwitchEnvironmentVariable = "RAILWAY_HOOKS_NOTIFY";

    /// <summary>
    /// Environment variable that overrides platform detection
    /// (<c>macos</c>|<c>linux</c>|<c>windows</c>) so tests can assert each platform's command line.
    /// </summary>
    public const string PlatformOverrideEnvironmentVariable = "RAILWAY_HOOKS_NOTIFY_PLATFORM";

    /// <summary>
    /// True when the <see cref="KillSwitchEnvironmentVariable"/> global kill switch is set to <c>0</c>.
    /// </summary>
    public static bool IsDisabledByEnvironment() =>
        string.Equals(
            Environment.GetEnvironmentVariable(KillSwitchEnvironmentVariable),
            "0",
            StringComparison.Ordinal);

    /// <summary>
    /// Applies the suppression matrix: notifications are skipped when the caller passed
    /// <paramref name="notify"/> = <see langword="false"/> (<c>--no-notify</c>), when the pipeline
    /// requested <see cref="NotificationLevel.Never"/>, or when the pipeline requested the default
    /// <see cref="NotificationLevel.OnFailure"/> and the run did not fail.
    /// </summary>
    /// <param name="notify">The caller's flag (<c>false</c> for <c>--no-notify</c>).</param>
    /// <param name="level">The pipeline's <c>notify:</c> preference.</param>
    /// <param name="failed">True when the run failed.</param>
    /// <returns>True when the notification should be sent.</returns>
    public static bool ShouldNotify(bool notify, NotificationLevel level, bool failed)
    {
        if (!notify)
        {
            return false;
        }

        return level switch
        {
            NotificationLevel.Never => false,
            NotificationLevel.Always => true,
            NotificationLevel.OnFailure => failed,
            _ => false,
        };
    }

    /// <summary>
    /// Resolves the notification platform from <see cref="PlatformOverrideEnvironmentVariable"/> when
    /// set, otherwise from the actual operating system.
    /// </summary>
    /// <returns>The selected platform, or <see langword="null"/> when it is unsupported/unknown.</returns>
    public static NotificationPlatform? ResolvePlatform()
    {
        var overrideValue = Environment.GetEnvironmentVariable(PlatformOverrideEnvironmentVariable)?.Trim();

        if (!string.IsNullOrEmpty(overrideValue))
        {
            return overrideValue.ToLowerInvariant() switch
            {
                "macos" or "mac" or "osx" or "darwin" => NotificationPlatform.MacOS,
                "linux" => NotificationPlatform.Linux,
                "windows" or "win" => NotificationPlatform.Windows,
                _ => null,
            };
        }

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
/// The executable and verbatim argument vector for one platform's notification one-liner.
/// </summary>
/// <param name="FileName">The executable to launch (no shell involved).</param>
/// <param name="Arguments">The argument vector, already escaped for its interpreter.</param>
internal sealed record NotificationCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Builds the exact command for <paramref name="platform"/>.</summary>
    /// <param name="platform">The target platform.</param>
    /// <param name="title">Notification title.</param>
    /// <param name="message">Notification body.</param>
    public static NotificationCommand For(NotificationPlatform platform, string title, string message) =>
        platform switch
        {
            // osascript -e 'display notification "<message>" with title "<title>"'
            NotificationPlatform.MacOS => new(
                "osascript",
                ["-e", $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\""]),

            // notify-send "<title>" "<message>" (no shell: the values are passed verbatim)
            NotificationPlatform.Linux => new("notify-send", [title, message]),

            // powershell -NoProfile -NonInteractive -Command "<toast script>"
            NotificationPlatform.Windows => new(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-Command", BuildWindowsToastScript(title, message)]),

            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown notification platform."),
        };

    /// <summary>Escapes <c>\</c> and <c>"</c> so the value stays a valid AppleScript string literal.</summary>
    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Escapes <c>'</c> for a PowerShell single-quoted string (other characters are literal).</summary>
    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// Builds a single-line PowerShell toast script using the WinRT toast API. Values are embedded in
    /// single-quoted strings, so only <c>'</c> needs escaping — robust against quotes and backslashes.
    /// </summary>
    private static string BuildWindowsToastScript(string title, string message) =>
        string.Join(
            ';',
            "$ErrorActionPreference='Stop'",
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null",
            "$template=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)",
            "$texts=$template.GetElementsByTagName('text')",
            $"$texts.Item(0).AppendChild($template.CreateTextNode('{EscapePowerShell(title)}')) | Out-Null",
            $"$texts.Item(1).AppendChild($template.CreateTextNode('{EscapePowerShell(message)}')) | Out-Null",
            "$toast=[Windows.UI.Notifications.ToastNotification]::new($template)",
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Railway').Show($toast)");
}
