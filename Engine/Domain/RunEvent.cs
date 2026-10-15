namespace Railway.Engine.Domain;

public enum RunEventKind
{
    RunStarted, JobStarted, StepStarted,
    StandardOutput, StandardError,
    StepCompleted, JobCompleted, RunCompleted
}

public sealed class RunEvent
{
    public RunEventKind Kind { get; init; }
    public int RunId { get; init; }
    public string? JobName { get; init; }
    public string? StepName { get; init; }
    public string? Text { get; init; }      // output lines; step command when verbose
    public int? ExitCode { get; init; }
    public RunStatus? Status { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
