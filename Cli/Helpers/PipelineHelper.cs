using Cli.Domain;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cli.Helpers;

public static class PipelineHelper
{
    private static readonly string[] Extensions = [".yml", ".yaml"];

    public static async Task<Pipeline> Parse(string path)
    {
        var yaml = await File.ReadAllTextAsync(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<Pipeline>(yaml);
    }

    public static async Task<(Pipeline? Pipeline, string? Error)> TryParse(string path)
    {
        try
        {
            return (await Parse(path), null);
        }
        catch (Exception exception)
        {
            return (null, exception.Message);
        }
    }

    public static IEnumerable<string> EnumeratePipelineFiles(string pipelinesPath)
    {
        if (!Directory.Exists(pipelinesPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(pipelinesPath, "*", SearchOption.AllDirectories)
            .Where(IsYaml)
            .OrderBy(path => path);
    }

    public static bool IsYaml(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Legacy methods
    private const string EnvKey = "RAILWAY_PIPELINE_PATH";

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
