namespace Railway.Engine.Domain;

/// <summary>
/// The pipeline trigger events a pipeline can subscribe to. The YAML keywords are
/// <c>post-commit</c>, <c>push</c>, <c>post-merge</c>, <c>post-checkout</c>,
/// <c>schedule</c> and <c>watch</c>; keyword mapping lives in
/// <see cref="Helpers.HookScriptHelper.GetEventKeyword"/>.
/// </summary>
public enum TriggerEvent
{
    /// <summary>Fired after a commit is created (git <c>post-commit</c>).</summary>
    PostCommit,

    /// <summary>Fired on push (git <c>pre-push</c>); supports branch/tag filters.</summary>
    PrePush,

    /// <summary>Fired after a successful merge (git <c>post-merge</c>).</summary>
    PostMerge,

    /// <summary>Fired after a branch checkout (git <c>post-checkout</c>).</summary>
    PostCheckout,

    /// <summary>Cron-style schedule (OS scheduler; Phase 2).</summary>
    Schedule,

    /// <summary>File watcher trigger (Phase 4).</summary>
    Watch
}
