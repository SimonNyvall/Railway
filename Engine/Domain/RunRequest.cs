namespace Railway.Engine.Domain;

public sealed class RunRequest
{
    public required string RepositoryPath { get; init; }
    public required string PipelineFileName { get; init; }
    public required Pipeline Pipeline { get; init; }
    public bool Verbose { get; init; }

    /// <summary>Provenance key: what caused the run (<c>manual</c>, <c>hook</c>, <c>schedule</c>, <c>watch</c>).</summary>
    public string Trigger { get; init; } = "manual";

    /// <summary>Optional human-readable trigger context (branch/sha/cron/glob).</summary>
    public string? TriggerDetail { get; init; }
}

/// <summary>Provenance describing what caused a run, flowing into <see cref="RunRequest"/> and the persisted <see cref="Run"/>.</summary>
public sealed record RunTrigger(string Key, string? Detail = null)
{
    /// <summary>The default trigger for runs started explicitly by a user.</summary>
    public static RunTrigger Manual { get; } = new("manual");
}
