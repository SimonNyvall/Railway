using System.Globalization;

using Railway.Engine.Domain;
using Railway.Engine.Services.Interfaces;

using YamlDotNet.RepresentationModel;

namespace Railway.Engine.Services;

/// <inheritdoc/>
public class PipelineConvertService : IPipelineConvertService
{
    private static readonly HashSet<string> GitLabReservedKeys = new(StringComparer.Ordinal)
    {
        "stages", "variables", "image", "before_script", "after_script",
        "default", "include", "workflow", "services", "cache", "hooks",
        "extends", "spec"
    };

    /// <inheritdoc/>
    public SourceFormat? DetectFormat(string sourcePath)
    {
        var fromPath = DetectFromPath(sourcePath);
        if (fromPath is not null)
        {
            return fromPath;
        }

        if (!TryLoadRoot(sourcePath, out var root, out _))
        {
            return null;
        }

        return DetectFromContent(root);
    }

    /// <inheritdoc/>
    public ConversionOutcome Convert(string sourcePath, SourceFormat format)
    {
        if (!TryLoadRoot(sourcePath, out var root, out var loadError))
        {
            return new ConversionOutcome(null, [], loadError);
        }

        var warnings = new List<string>();

        (Pipeline? pipeline, string? convertError) = format switch
        {
            SourceFormat.GitHub => ConvertGitHub(root, sourcePath, warnings),
            SourceFormat.GitLab => ConvertGitLab(root, sourcePath, warnings),
            SourceFormat.Azure => ConvertAzure(root, sourcePath, warnings),
            _ => ((Pipeline?)null, (string?)$"Unsupported source format value: {format}.")
        };

        return new ConversionOutcome(pipeline, warnings, convertError);
    }

    // ------------------------------------------------------------------
    // Source loading
    // ------------------------------------------------------------------

    private static bool TryLoadRoot(string sourcePath, out YamlMappingNode root, out string? error)
    {
        root = null!;
        error = null;

        if (!File.Exists(sourcePath))
        {
            error = $"Source file not found: {sourcePath}";
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(sourcePath);
        }
        catch (Exception exception)
        {
            error = $"Source file could not be read: {exception.Message}";
            return false;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode rootMapping)
            {
                error = "Source is not recognizable CI YAML: the document root must be a mapping.";
                return false;
            }

            root = rootMapping;
            return true;
        }
        catch (Exception exception)
        {
            // Deliberately broad: any scanner/parser/alias failure makes the source unconvertible,
            // and Convert reports it as an outcome error instead of throwing.
            error = $"Source is not valid YAML: {exception.Message}";
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Format detection
    // ------------------------------------------------------------------

    private static SourceFormat? DetectFromPath(string sourcePath)
    {
        var normalized = sourcePath.Replace('\\', '/');
        if (normalized.Contains(".github/workflows/", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.GitHub;
        }

        var fileName = Path.GetFileName(normalized);
        if (fileName.StartsWith(".gitlab-ci", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.GitLab;
        }

        if (fileName.StartsWith("azure-pipelines", StringComparison.OrdinalIgnoreCase))
        {
            return SourceFormat.Azure;
        }

        return null;
    }

    private static SourceFormat? DetectFromContent(YamlMappingNode root)
    {
        var matches = new List<SourceFormat>();

        if (HasKey(root, "on") && HasKeyAnywhere(root, "runs-on"))
        {
            matches.Add(SourceFormat.GitHub);
        }

        if (HasKeyAnywhere(root, "pool") || HasKey(root, "trigger") || StagesContainStageBlocks(root))
        {
            matches.Add(SourceFormat.Azure);
        }

        if (HasKey(root, "stages")
            && (HasKeyAnywhere(root, "script") || HasKeyAnywhere(root, "image"))
            && !HasKeyAnywhere(root, "pool")
            && StagesArePlainStrings(root))
        {
            matches.Add(SourceFormat.GitLab);
        }

        // Zero matches = failed detection, more than one = ambiguous; both are reported as null.
        return matches.Count == 1 ? matches[0] : (SourceFormat?)null;
    }

    private static bool StagesContainStageBlocks(YamlMappingNode root) =>
        GetStagesSequence(root)?.Children.Any(child => child is YamlMappingNode stage && HasKey(stage, "stage")) ?? false;

    private static bool StagesArePlainStrings(YamlMappingNode root)
    {
        var stages = GetStagesSequence(root);
        return stages is { Children.Count: > 0 } && stages.Children.All(child => child is YamlScalarNode);
    }

    // ------------------------------------------------------------------
    // GitHub Actions
    // ------------------------------------------------------------------

    private static (Pipeline? Pipeline, string? Error) ConvertGitHub(YamlMappingNode root, string sourcePath, List<string> warnings)
    {
        var pipeline = new Pipeline { Name = PipelineName(root, sourcePath) };

        if (TryGet(root, "env") is { } workflowEnv)
        {
            MapVariables(workflowEnv, "env", string.Empty, pipeline.Variables, warnings);
        }

        foreach (var (key, _) in root.Children)
        {
            var rootKey = KeyText(key);
            if (rootKey is "name" or "env" or "jobs")
            {
                continue;
            }

            warnings.Add(RootWarning(rootKey));
        }

        if (TryGet(root, "jobs") is not YamlMappingNode jobsNode)
        {
            return (null, "Source is not a recognizable GitHub Actions workflow: missing 'jobs' mapping.");
        }

        var pendingNeeds = new List<(string JobKey, List<string> Needs)>();

        foreach (var (key, value) in jobsNode.Children)
        {
            var jobKey = KeyText(key);
            if (value is not YamlMappingNode jobNode)
            {
                warnings.Add($"{JobPrefix(jobKey)}definition is not a mapping and cannot be converted");
                continue;
            }

            var (job, needs) = ConvertGitHubJob(jobNode, jobKey, warnings);
            if (job is null)
            {
                continue;
            }

            if (!pipeline.Jobs.TryAdd(jobKey, job))
            {
                warnings.Add($"{JobPrefix(jobKey)}duplicate job key ignored");
                continue;
            }

            pendingNeeds.Add((jobKey, needs));
        }

        if (pipeline.Jobs.Count == 0)
        {
            return (null, NoConvertibleJobsError);
        }

        foreach (var (jobKey, needs) in pendingNeeds)
        {
            foreach (var dependency in needs)
            {
                if (pipeline.Jobs.ContainsKey(dependency))
                {
                    pipeline.Jobs[jobKey].DependsOn.Add(dependency);
                }
                else
                {
                    warnings.Add($"{JobPrefix(jobKey)}dependency '{dependency}' was not converted and is ignored");
                }
            }
        }

        return (pipeline, null);
    }

    private static (Job? Job, List<string> Needs) ConvertGitHubJob(YamlMappingNode node, string jobKey, List<string> warnings)
    {
        var job = new Job();
        var needs = new List<string>();
        var steps = new List<Step>();

        foreach (var (key, value) in node.Children)
        {
            switch (KeyText(key))
            {
                case "needs":
                    needs = MapNeeds(value, jobKey, warnings);
                    break;
                case "env":
                    MapVariables(value, "env", JobPrefix(jobKey), job.Env, warnings);
                    break;
                case "timeout-minutes":
                    job.Timeout = MapTimeout(value, "timeout-minutes", JobPrefix(jobKey), warnings) ?? job.Timeout;
                    break;
                case "steps":
                    steps = ConvertSteps(value, jobKey, warnings);
                    break;
                default:
                    warnings.Add(JobWarning(jobKey, KeyText(key)));
                    break;
            }
        }

        return TryCompleteJob(job, jobKey, steps, warnings) ? (job, needs) : (null, []);
    }

    // ------------------------------------------------------------------
    // GitLab CI
    // ------------------------------------------------------------------

    private static (Pipeline? Pipeline, string? Error) ConvertGitLab(YamlMappingNode root, string sourcePath, List<string> warnings)
    {
        var pipeline = new Pipeline { Name = PipelineName(root, sourcePath) };
        List<string> stagesOrder = [];
        var candidates = new List<(string JobKey, YamlMappingNode JobNode)>();

        foreach (var (key, value) in root.Children)
        {
            var rootKey = KeyText(key);
            if (rootKey == "stages")
            {
                stagesOrder = ReadPlainStages(value, warnings);
            }
            else if (rootKey == "variables")
            {
                MapVariables(value, "variables", string.Empty, pipeline.Variables, warnings);
            }
            else if (value is YamlMappingNode jobNode && !GitLabReservedKeys.Contains(rootKey))
            {
                candidates.Add((rootKey, jobNode));
            }
            else
            {
                warnings.Add(RootWarning(rootKey));
            }
        }

        if (candidates.Count == 0)
        {
            return (null, "Source is not a recognizable GitLab CI configuration: no job definitions found at the top level.");
        }

        var jobStageIndex = new Dictionary<string, int>();

        foreach (var (jobKey, jobNode) in candidates)
        {
            var (job, stage) = ConvertGitLabJob(jobNode, jobKey, warnings);
            if (job is null)
            {
                continue;
            }

            if (!pipeline.Jobs.TryAdd(jobKey, job))
            {
                warnings.Add($"{JobPrefix(jobKey)}duplicate job key ignored");
                continue;
            }

            // GitLab's default stage is 'test'; an unknown or absent stage index (-1) is treated as first stage.
            jobStageIndex[jobKey] = stagesOrder.IndexOf(stage ?? "test");
        }

        if (pipeline.Jobs.Count == 0)
        {
            return (null, NoConvertibleJobsError);
        }

        ApplyStageDependencies(pipeline, jobStageIndex);

        return (pipeline, null);
    }

    private static (Job? Job, string? Stage) ConvertGitLabJob(YamlMappingNode node, string jobKey, List<string> warnings)
    {
        var job = new Job();
        string? stage = null;
        var steps = new List<Step>();

        foreach (var (key, value) in node.Children)
        {
            switch (KeyText(key))
            {
                case "stage":
                    if (value is not YamlScalarNode)
                    {
                        warnings.Add($"{JobPrefix(jobKey)}'stage' is not a scalar and cannot be mapped");
                    }

                    stage = AsScalar(value);
                    break;
                case "script":
                    steps = ConvertScript(value, jobKey, warnings);
                    break;
                case "variables":
                    MapVariables(value, "variables", JobPrefix(jobKey), job.Env, warnings);
                    break;
                case "env":
                    MapVariables(value, "env", JobPrefix(jobKey), job.Env, warnings);
                    break;
                default:
                    warnings.Add(JobWarning(jobKey, KeyText(key)));
                    break;
            }
        }

        return TryCompleteJob(job, jobKey, steps, warnings) ? (job, stage) : (null, stage);
    }

    private static List<string> ReadPlainStages(YamlNode? node, List<string> warnings)
    {
        if (node is not YamlSequenceNode sequence)
        {
            warnings.Add("'stages' is not a sequence and cannot be mapped");
            return [];
        }

        var stages = new List<string>();
        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is YamlScalarNode { Value: { Length: > 0 } } stage)
            {
                stages.Add(stage.Value);
            }
            else
            {
                warnings.Add($"'stages' entry #{index + 1} is not a scalar and cannot be mapped");
            }
        }

        return stages;
    }

    // ------------------------------------------------------------------
    // Azure DevOps
    // ------------------------------------------------------------------

    private static (Pipeline? Pipeline, string? Error) ConvertAzure(YamlMappingNode root, string sourcePath, List<string> warnings)
    {
        var pipeline = new Pipeline { Name = PipelineName(root, sourcePath) };
        YamlSequenceNode? stagesNode = null;
        YamlSequenceNode? topLevelJobsNode = null;

        foreach (var (key, value) in root.Children)
        {
            var rootKey = KeyText(key);
            switch (rootKey)
            {
                case "name":
                    break;
                case "variables":
                    MapVariables(value, "variables", string.Empty, pipeline.Variables, warnings);
                    break;
                case "stages":
                    if (value is YamlSequenceNode stageSequence)
                    {
                        stagesNode = stageSequence;
                    }
                    else
                    {
                        warnings.Add(RootWarning(rootKey));
                    }

                    break;
                case "jobs":
                    if (value is YamlSequenceNode jobsSequence)
                    {
                        topLevelJobsNode = jobsSequence;
                    }
                    else
                    {
                        warnings.Add(RootWarning(rootKey));
                    }

                    break;
                default:
                    warnings.Add(RootWarning(rootKey));
                    break;
            }
        }

        if (stagesNode is null && topLevelJobsNode is null)
        {
            return (null, "Source is not a recognizable Azure DevOps pipeline: missing 'stages' or 'jobs' sequence.");
        }

        var jobStageIndex = new Dictionary<string, int>();
        var stagesOrder = new List<string>();

        if (stagesNode is not null)
        {
            for (var stageIndex = 0; stageIndex < stagesNode.Children.Count; stageIndex++)
            {
                if (stagesNode.Children[stageIndex] is not YamlMappingNode stageNode)
                {
                    warnings.Add($"stages entry #{stageIndex + 1} is not a mapping and cannot be converted");
                    continue;
                }

                var orderIndex = stagesOrder.Count;
                stagesOrder.Add(AsScalar(TryGet(stageNode, "stage")) is { Length: > 0 } stageName
                    ? stageName
                    : $"stage #{stageIndex + 1}");
                var stageContext = $"stage '{stagesOrder[orderIndex]}': ";

                foreach (var (key, value) in stageNode.Children)
                {
                    var stageKey = KeyText(key);
                    if (stageKey == "stage")
                    {
                        continue;
                    }

                    if (stageKey != "jobs")
                    {
                        warnings.Add($"{stageContext}'{stageKey}' cannot be mapped");
                        continue;
                    }

                    if (value is not YamlSequenceNode jobsSequence)
                    {
                        warnings.Add($"{stageContext}'jobs' is not a sequence and cannot be converted");
                        continue;
                    }

                    ConvertAzureJobEntries(jobsSequence, orderIndex, stageContext, pipeline, jobStageIndex, warnings);
                }
            }
        }

        if (topLevelJobsNode is not null)
        {
            // Stage-less top-level jobs are treated as first-stage jobs (no dependencies).
            ConvertAzureJobEntries(topLevelJobsNode, -1, string.Empty, pipeline, jobStageIndex, warnings);
        }

        if (pipeline.Jobs.Count == 0)
        {
            return (null, NoConvertibleJobsError);
        }

        ApplyStageDependencies(pipeline, jobStageIndex);

        return (pipeline, null);
    }

    private static void ConvertAzureJobEntries(
        YamlSequenceNode jobsSequence,
        int stageIndex,
        string stageContext,
        Pipeline pipeline,
        Dictionary<string, int> jobStageIndex,
        List<string> warnings)
    {
        foreach (var entry in jobsSequence.Children)
        {
            if (entry is not YamlMappingNode jobNode || AsScalar(TryGet(jobNode, "job")) is not { Length: > 0 } jobKey)
            {
                warnings.Add($"{stageContext}jobs entry without a 'job' key cannot be converted");
                continue;
            }

            var job = ConvertAzureJob(jobNode, jobKey, warnings);
            if (job is null)
            {
                continue;
            }

            if (!pipeline.Jobs.TryAdd(jobKey, job))
            {
                warnings.Add($"{JobPrefix(jobKey)}duplicate job key ignored");
                continue;
            }

            jobStageIndex[jobKey] = stageIndex;
        }
    }

    private static Job? ConvertAzureJob(YamlMappingNode node, string jobKey, List<string> warnings)
    {
        var job = new Job();
        var steps = new List<Step>();

        foreach (var (key, value) in node.Children)
        {
            switch (KeyText(key))
            {
                case "job":
                    break; // consumed as the job key
                case "timeoutInMinutes":
                    job.Timeout = MapTimeout(value, "timeoutInMinutes", JobPrefix(jobKey), warnings) ?? job.Timeout;
                    break;
                case "env":
                    MapVariables(value, "env", JobPrefix(jobKey), job.Env, warnings);
                    break;
                case "variables":
                    MapVariables(value, "variables", JobPrefix(jobKey), job.Env, warnings);
                    break;
                case "steps":
                    steps = ConvertSteps(value, jobKey, warnings);
                    break;
                default:
                    warnings.Add(JobWarning(jobKey, KeyText(key)));
                    break;
            }
        }

        return TryCompleteJob(job, jobKey, steps, warnings) ? job : null;
    }

    // ------------------------------------------------------------------
    // Shared step/entry mapping
    // ------------------------------------------------------------------

    private static List<Step> ConvertSteps(YamlNode? node, string jobKey, List<string> warnings)
    {
        var steps = new List<Step>();

        if (node is not YamlSequenceNode sequence)
        {
            if (node is not null)
            {
                warnings.Add($"{JobPrefix(jobKey)}'steps' is not a sequence and cannot be converted");
            }

            return steps;
        }

        for (var index = 0; index < sequence.Children.Count; index++)
        {
            if (sequence.Children[index] is not YamlMappingNode stepNode)
            {
                warnings.Add($"{JobPrefix(jobKey)}step #{index + 1} is not a mapping and cannot be converted");
                continue;
            }

            var stepName = AsScalar(TryGet(stepNode, "name")) ?? AsScalar(TryGet(stepNode, "displayName"));
            var label = StepLabel(stepName, index);
            var prefix = $"{JobPrefix(jobKey)}step {label}: ";
            var step = new Step { Name = stepName };
            var hasRun = false;

            foreach (var (key, value) in stepNode.Children)
            {
                switch (KeyText(key))
                {
                    case "name":
                    case "displayName":
                        break; // already read for the step label
                    case "run":
                    case "script":
                        if (AsScalar(value) is { } run && !string.IsNullOrWhiteSpace(run))
                        {
                            step.Run = run;
                            hasRun = true;
                        }
                        else
                        {
                            warnings.Add($"{prefix}'{KeyText(key)}' has no value (step skipped)");
                        }

                        break;
                    case "env":
                        MapVariables(value, "env", prefix, step.Env, warnings);
                        break;
                    case "timeout-minutes":
                    case "timeoutInMinutes":
                        step.Timeout = MapTimeout(value, KeyText(key), prefix, warnings) ?? step.Timeout;
                        break;
                    default:
                        warnings.Add($"{prefix}'{KeyText(key)}' cannot be mapped");
                        break;
                }
            }

            if (hasRun)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static List<Step> ConvertScript(YamlNode? node, string jobKey, List<string> warnings)
    {
        var steps = new List<Step>();

        if (node is YamlScalarNode scalar)
        {
            if (!string.IsNullOrWhiteSpace(scalar.Value))
            {
                steps.Add(new Step { Run = scalar.Value });
            }
            else
            {
                warnings.Add($"{JobPrefix(jobKey)}'script' has no value (step skipped)");
            }

            return steps;
        }

        if (node is YamlSequenceNode sequence)
        {
            for (var index = 0; index < sequence.Children.Count; index++)
            {
                // One Railway step per script list entry (v1 choice; see the interface mapping table).
                if (sequence.Children[index] is YamlScalarNode { Value: not null } entry && !string.IsNullOrWhiteSpace(entry.Value))
                {
                    steps.Add(new Step { Run = entry.Value });
                }
                else
                {
                    warnings.Add($"{JobPrefix(jobKey)}'script' entry #{index + 1} is not a scalar command and cannot be mapped");
                }
            }

            return steps;
        }

        if (node is not null)
        {
            warnings.Add($"{JobPrefix(jobKey)}'script' is not a scalar or sequence and cannot be mapped");
        }

        return steps;
    }

    // ------------------------------------------------------------------
    // Shared mapping helpers
    // ------------------------------------------------------------------

    private const string NoConvertibleJobsError = "Source contains no convertible jobs: no job produced at least one step.";

    private static bool TryCompleteJob(Job job, string jobKey, List<Step> steps, List<string> warnings)
    {
        if (steps.Count == 0)
        {
            warnings.Add($"{JobPrefix(jobKey)}no convertible steps found (job skipped)");
            return false;
        }

        job.Steps = steps;
        return true;
    }

    private static void ApplyStageDependencies(Pipeline pipeline, IReadOnlyDictionary<string, int> jobStageIndex)
    {
        var groups = new Dictionary<int, List<string>>();

        foreach (var jobKey in pipeline.Jobs.Keys)
        {
            var stageIndex = jobStageIndex.TryGetValue(jobKey, out var index) ? index : -1;
            if (!groups.TryGetValue(stageIndex, out var group))
            {
                group = [];
                groups[stageIndex] = group;
            }

            group.Add(jobKey);
        }

        foreach (var jobKey in pipeline.Jobs.Keys)
        {
            var stageIndex = jobStageIndex.TryGetValue(jobKey, out var index) ? index : -1;
            if (stageIndex > 0 && groups.TryGetValue(stageIndex - 1, out var previousStage) && previousStage.Count > 0)
            {
                pipeline.Jobs[jobKey].DependsOn.AddRange(previousStage);
            }
        }
    }

    private static List<string> MapNeeds(YamlNode? node, string jobKey, List<string> warnings)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { Length: > 0 } } scalar:
                return [scalar.Value];
            case YamlSequenceNode sequence:
                var dependencies = new List<string>();
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    if (sequence.Children[index] is YamlScalarNode { Value: { Length: > 0 } } entry)
                    {
                        dependencies.Add(entry.Value);
                    }
                    else
                    {
                        warnings.Add($"{JobPrefix(jobKey)}'needs' entry #{index + 1} is not a scalar and cannot be mapped");
                    }
                }

                return dependencies;
            default:
                warnings.Add(JobWarning(jobKey, "needs"));
                return [];
        }
    }

    private static void MapVariables(
        YamlNode? node,
        string constructKey,
        string prefix,
        IDictionary<string, string> target,
        List<string> warnings)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    var variableName = KeyText(key);
                    if (value is YamlScalarNode { Value: not null } scalar)
                    {
                        target[variableName] = scalar.Value;
                    }
                    else
                    {
                        warnings.Add($"{prefix}'{constructKey}' entry '{variableName}' is not a scalar and cannot be mapped");
                    }
                }

                break;

            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    var entry = sequence.Children[index];
                    if (entry is not YamlMappingNode entryNode
                        || AsScalar(TryGet(entryNode, "name")) is not { Length: > 0 } name
                        || AsScalar(TryGet(entryNode, "value")) is not { } value)
                    {
                        warnings.Add($"{prefix}'{constructKey}' entry #{index + 1} must define scalar 'name' and 'value' and cannot be mapped");
                        continue;
                    }

                    target[name] = value;
                }

                break;

            default:
                warnings.Add($"{prefix}'{constructKey}' cannot be mapped");
                break;
        }
    }

    private static TimeSpan? MapTimeout(YamlNode? node, string key, string prefix, List<string> warnings)
    {
        if (node is YamlScalarNode scalar
            && scalar.Value is not null
            && double.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        warnings.Add($"{prefix}'{key}' is not a number of minutes and cannot be mapped");
        return null;
    }

    private static string PipelineName(YamlMappingNode root, string sourcePath) =>
        AsScalar(TryGet(root, "name")) is { Length: > 0 } name
            ? name
            : Path.GetFileNameWithoutExtension(sourcePath);

    // ------------------------------------------------------------------
    // YAML node helpers
    // ------------------------------------------------------------------

    private static YamlNode? TryGet(YamlMappingNode node, string key)
    {
        foreach (var (childKey, value) in node.Children)
        {
            if (string.Equals(KeyText(childKey), key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasKey(YamlMappingNode node, string key) => TryGet(node, key) is not null;

    private static bool HasKeyAnywhere(YamlNode node, string key)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (childKey, value) in mapping.Children)
                {
                    if (string.Equals(KeyText(childKey), key, StringComparison.Ordinal) || HasKeyAnywhere(value, key))
                    {
                        return true;
                    }
                }

                return false;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                {
                    if (HasKeyAnywhere(child, key))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static YamlSequenceNode? GetStagesSequence(YamlMappingNode root) => TryGet(root, "stages") as YamlSequenceNode;

    private static string KeyText(YamlNode key) =>
        key is YamlScalarNode scalar ? scalar.Value ?? string.Empty : key.ToString();

    private static string? AsScalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    // ------------------------------------------------------------------
    // Warning message helpers
    // ------------------------------------------------------------------

    private static string RootWarning(string key) => $"'{key}' cannot be mapped";

    private static string JobPrefix(string jobKey) => $"job '{jobKey}': ";

    private static string JobWarning(string jobKey, string key) => $"{JobPrefix(jobKey)}'{key}' cannot be mapped";

    private static string StepLabel(string? stepName, int index) =>
        stepName is { Length: > 0 } ? $"'{stepName}'" : $"#{index + 1}";
}
