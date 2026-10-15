using System.ComponentModel.DataAnnotations;

namespace Cli.Domain;

public class Run
{
    public int Id { get; set; }
    public int RepositoryId { get; set; }
    [MaxLength(100)]
    public string PipelineFileName { get; set; } = null!;
    public string PipelineName { get; set; } = null!;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public ICollection<RunJob> Jobs { get; } = [];
}
