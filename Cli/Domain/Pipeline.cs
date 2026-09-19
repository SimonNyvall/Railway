using System.ComponentModel.DataAnnotations;

namespace Cli.Domain;

public class Pipeline
{
	public int Id { get; set; }
	[MaxLength(100)]
	public string Path { get; set; } = null!;
	public ICollection<RepositoryPipeline> RepositoryPipelines { get; } = [];
}
