using Cli.Domain;

using YamlDotNet.Serialization;

using YamlDotNet.Serialization.NamingConventions;

namespace Cli.Helpers;

public static class PipelineHelper
{
	private const string EnvKey = "RAILWAY_PIPELINE_PATH";

    public static async Task<Pipeline> Parse(string path)
    {
        var yaml = await File.ReadAllTextAsync(path);

		var deserializer = new DeserializerBuilder()
			.WithNamingConvention(UnderscoredNamingConvention.Instance)
			.IgnoreUnmatchedProperties()
			.Build();

		return deserializer.Deserialize<Pipeline>(yaml);
    }

	public static string[] GetPipelineNames()
	{
		var path = GetPipelinePath();

        return Directory
            .GetFiles(path, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToArray()!;
	}

	private static string GetPipelinePath()
	{
		var path = Environment.GetEnvironmentVariable(EnvKey);

		if (path == null)
			throw new InvalidOperationException($"The ENV key: {EnvKey} must be set to a location.");

		return path;
	}
}