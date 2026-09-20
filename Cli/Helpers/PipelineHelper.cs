namespace Cli.Helpers;

public static class PipelineHelper
{
	private const string EnvKey = "RAILWAY_PIPELINE_PATH";
	
	public static string[] GetPipelineNames()
	{
		var path = GetPipelinePath();
		
		var directoryNames = Directory.GetDirectories(path);
		
		return directoryNames;
	}
	
	private static string GetPipelinePath()
	{
		var path = Environment.GetEnvironmentVariable(EnvKey);
		
		if (path == null)
			throw new InvalidOperationException($"The ENV key: {EnvKey} must be set to a location.");
		
		return path;	
	}	
}
