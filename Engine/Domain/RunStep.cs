namespace Railway.Engine.Domain;

public class RunStep
{
    public int Id { get; set; }
    public int RunJobId { get; set; }
    public RunJob Job { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Command { get; set; } = null!;
    public RunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
}
