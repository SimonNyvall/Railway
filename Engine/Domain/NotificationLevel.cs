namespace Railway.Engine.Domain;

/// <summary>
/// When a pipeline wants desktop notifications about its runs.
/// <see cref="OnFailure"/> is the default (<c>notify:</c> is omitted in most YAML).
/// </summary>
public enum NotificationLevel
{
    /// <summary>Notify only when a run fails (default).</summary>
    OnFailure = 0,

    /// <summary>Never notify.</summary>
    Never,

    /// <summary>Notify on success and failure alike.</summary>
    Always
}
