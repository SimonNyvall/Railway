using Railway.Engine.Domain;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Railway.Engine.Helpers;

public static class RailwayManifestHelper
{
    public static async Task<RailwayManifest> Read(string railwayPath)
    {
        var manifestPath = RepositoryHelper.GetManifestPath(railwayPath);

        if (!File.Exists(manifestPath))
        {
            return new RailwayManifest();
        }

        var yaml = await File.ReadAllTextAsync(manifestPath);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new RailwayManifest();
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<RailwayManifest>(yaml) ?? new RailwayManifest();
    }

    public static async Task Write(string railwayPath, RailwayManifest manifest)
    {
        var manifestPath = RepositoryHelper.GetManifestPath(railwayPath);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var yaml = serializer.Serialize(manifest);

        await File.WriteAllTextAsync(manifestPath, yaml);
    }
}
