using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

/// <summary>
/// Sends desktop notifications about pipeline runs (macOS <c>osascript</c>, Linux <c>notify-send</c>,
/// Windows PowerShell toast), degrading gracefully when the platform has no notifier.
/// </summary>
/// <remarks>
/// Implemented in a later task (Phase 1 step 1.7). <c>HookRunner</c> depends on this contract
/// optionally, so hook runs keep working before the implementation and registration land.
/// Implementations must never throw for a failed notification.
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// Notifies the user about a run.
    /// </summary>
    /// <param name="title">Short notification title.</param>
    /// <param name="message">Notification body.</param>
    /// <param name="level">
    /// The pipeline's notification preference; implementations may use it for the notification's
    /// urgency, but suppression is decided by the caller.
    /// </param>
    Task NotifyAsync(string title, string message, NotificationLevel level);
}
