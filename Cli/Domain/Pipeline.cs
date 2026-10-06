using System.ComponentModel.DataAnnotations;

namespace Cli.Domain;

public class Pipeline
{
	public int Id { get; set; }
	[MaxLength(100)]
	public string Path { get; set; } = null!;
	public string Name { get; set; } = null!;
	public Dictionary<string, Job> Jobs { get; set; } = [];
	public Dictionary<string, string> Variables { get; set; } = [];
	public ICollection<RepositoryPipeline> RepositoryPipelines { get; } = [];
}
