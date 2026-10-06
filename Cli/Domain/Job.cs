namespace Cli.Domain;

public class Job
{
	public List<string> DependsOn { get; set; } = [];
	public Dictionary<string, string> Env { get; set; } = [];
	public TimeSpan? Timeout { get; set; }
	public List<Step> Steps { get; set; } = [];
	public List<string> Locks { get; set; } = [];
}
