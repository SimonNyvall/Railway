namespace Cli.Domain;

public class Step
{
	public string? Name { get; set; }
	public string? Run { get; set; }
	public Dictionary<string, string> Env { get; set; } = [];
	public TimeSpan Timeout { get; set; }
}
