namespace Cli.Domain;

public class RepositoryPipeline
{
	public int RepositoryId { get; set; }
	public int PipelineId { get; set; }
	public Pipeline Pipeline { get; set; } = null!;
	public Repository Repository { get; set; } = null!;
}
