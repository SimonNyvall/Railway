using Railway.Engine.Domain;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Railway.Engine.Helpers;

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

    /// <summary>
    /// Serializes a <see cref="Pipeline"/> to YAML and writes it to <paramref name="path"/>.
    /// </summary>
    /// <param name="pipeline">The pipeline to write.</param>
    /// <param name="path">The full path of the file to write.</param>
    /// <param name="ct">A token to observe while writing the file.</param>
    /// <remarks>
    /// <para>
    /// The serializer is configured with the same <see cref="UnderscoredNamingConvention"/> used by
    /// <see cref="Parse(string)"/> and emits exactly the railway pipeline schema: <c>name</c>, <c>jobs</c>, and
    /// <c>variables</c> for a pipeline; <c>depends_on</c>, <c>env</c>, <c>timeout</c>, <c>steps</c>, and <c>locks</c>
    /// for a job; and <c>name</c>, <c>run</c>, <c>env</c>, and <c>timeout</c> for a step.
    /// </para>
    /// <para>
    /// Serialization goes through private write models rather than <see cref="Pipeline"/> directly so persistence-only
    /// members (<see cref="Pipeline.Id"/>, <see cref="Pipeline.Path"/>, <see cref="Pipeline.RepositoryPipelines"/>)
    /// never reach the file, and default values (a zero <see cref="Step.Timeout"/>, a <see langword="null"/> job
    /// timeout) and empty collections are omitted.
    /// </para>
    /// <example>
    /// The written file round-trips through <see cref="Parse(string)"/> into an equivalent pipeline:
    /// <code language="csharp">
    /// await PipelineHelper.Write(pipeline, path);
    /// var reparsed = await PipelineHelper.Parse(path);
    /// </code>
    /// </example>
    /// </remarks>
    public static async Task Write(Pipeline pipeline, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithIndentedSequences()
            .ConfigureDefaultValuesHandling(
                DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        var yaml = serializer.Serialize(PipelineWriteModel.From(pipeline));

        await File.WriteAllTextAsync(path, yaml, ct);
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

    // Whitelist write models: only these members are visible to the serializer, which keeps EF
    // navigation/scalar properties out of the output. They mirror the railway pipeline schema.
    private sealed class PipelineWriteModel
    {
        public string Name { get; init; } = string.Empty;

        public Dictionary<string, JobWriteModel> Jobs { get; init; } = [];

        public Dictionary<string, string> Variables { get; init; } = [];

        public static PipelineWriteModel From(Pipeline pipeline) => new()
        {
            Name = pipeline.Name,
            Jobs = pipeline.Jobs.ToDictionary(pair => pair.Key, pair => JobWriteModel.From(pair.Value)),
            Variables = new Dictionary<string, string>(pipeline.Variables),
        };
    }

    private sealed class JobWriteModel
    {
        public List<string> DependsOn { get; init; } = [];

        public Dictionary<string, string> Env { get; init; } = [];

        public TimeSpan? Timeout { get; init; }

        public List<StepWriteModel> Steps { get; init; } = [];

        public List<string> Locks { get; init; } = [];

        public static JobWriteModel From(Job job) => new()
        {
            DependsOn = [.. job.DependsOn],
            Env = new Dictionary<string, string>(job.Env),
            Timeout = job.Timeout,
            Steps = job.Steps.Select(StepWriteModel.From).ToList(),
            Locks = [.. job.Locks],
        };
    }

    private sealed class StepWriteModel
    {
        public string? Name { get; init; }

        public string? Run { get; init; }

        public Dictionary<string, string> Env { get; init; } = [];

        public TimeSpan? Timeout { get; init; }

        public static StepWriteModel From(Step step) => new()
        {
            Name = step.Name,
            Run = step.Run,
            Env = new Dictionary<string, string>(step.Env),

            // Step.Timeout is non-nullable, so a zero value is the "unset" default and must be omitted.
            Timeout = step.Timeout == TimeSpan.Zero ? null : step.Timeout,
        };
    }
}
