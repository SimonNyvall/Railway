namespace Railway.Engine.Domain;

public class RunJob
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public Run Run { get; set; } = null!;
    public string Name { get; set; } = null!;
    public RunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public ICollection<RunStep> Steps { get; } = [];
}
