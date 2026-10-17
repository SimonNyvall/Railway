using Railway.Engine.Domain;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
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
            .WithTypeConverter(new TriggerListYamlConverter())
            .WithTypeConverter(new NotificationLevelYamlConverter())
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
    /// <see cref="Parse(string)"/> and emits exactly the railway pipeline schema: <c>name</c>, <c>jobs</c>,
    /// <c>variables</c>, <c>triggers</c>, and <c>notify</c> for a pipeline; <c>depends_on</c>, <c>env</c>,
    /// <c>timeout</c>, <c>steps</c>, and <c>locks</c> for a job; and <c>name</c>, <c>run</c>, <c>env</c>,
    /// and <c>timeout</c> for a step.
    /// </para>
    /// <para>
    /// Serialization goes through private write models rather than <see cref="Pipeline"/> directly so persistence-only
    /// members (<see cref="Pipeline.Id"/>, <see cref="Pipeline.Path"/>, <see cref="Pipeline.RepositoryPipelines"/>)
    /// never reach the file, and default values (a zero <see cref="Step.Timeout"/>, a <see langword="null"/> job
    /// timeout, the default <see cref="NotificationLevel.OnFailure"/> notify level) and empty collections
    /// (including an empty trigger list) are omitted.
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
            .WithTypeConverter(new TriggerListYamlConverter())
            .WithTypeConverter(new NotificationLevelYamlConverter())
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

        public List<TriggerDefinition> Triggers { get; init; } = [];

        public NotificationLevel Notify { get; init; } = NotificationLevel.OnFailure;

        public static PipelineWriteModel From(Pipeline pipeline) => new()
        {
            Name = pipeline.Name,
            Jobs = pipeline.Jobs.ToDictionary(pair => pair.Key, pair => JobWriteModel.From(pair.Value)),
            Variables = new Dictionary<string, string>(pipeline.Variables),
            Triggers = [.. pipeline.Triggers],
            Notify = pipeline.Notify,
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

    /// <summary>
    /// Reads and writes the <c>triggers:</c> wire shape: a sequence whose entries are either a scalar
    /// event name or a single-key map (<c>push: {…}</c>, <c>schedule: "…"</c>, <c>watch: […]</c>).
    /// Deserialization collects validation problems in <see cref="TriggerDefinition.Errors"/> instead
    /// of throwing; serialization emits only entries without errors and omits default options.
    /// </summary>
    private sealed class TriggerListYamlConverter : IYamlTypeConverter
    {
        // The event<->keyword vocabulary is owned by HookScriptHelper (the single source it shares with
        // the generated git-hook wrappers); the converter only derives its lookups, so the YAML wire
        // format can never drift from the keywords the wrappers emit.
        private static readonly Dictionary<string, TriggerEvent> EventsByName =
            Enum.GetValues<TriggerEvent>().ToDictionary(
                HookScriptHelper.GetEventKeyword,
                triggerEvent => triggerEvent,
                StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<TriggerEvent, string> NamesByEvent =
            Enum.GetValues<TriggerEvent>().ToDictionary(
                triggerEvent => triggerEvent,
                HookScriptHelper.GetEventKeyword);

        public bool Accepts(Type type) => type == typeof(List<TriggerDefinition>);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.Accept<Scalar>(out _))
            {
                // A bare scalar (`triggers: post-commit`) is tolerated as a single entry;
                // a null value (`triggers:` with nothing after it) means "no triggers".
                var scalar = parser.Consume<Scalar>();
                if (IsNullScalar(scalar))
                {
                    return new List<TriggerDefinition>();
                }

                return new List<TriggerDefinition> { ReadEntry(scalar.Value) };
            }

            if (!parser.Accept<SequenceStart>(out _))
            {
                throw new YamlException("The 'triggers' value must be a sequence of trigger entries.");
            }

            parser.Consume<SequenceStart>();

            var triggers = new List<TriggerDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!parser.Accept<SequenceEnd>(out _))
            {
                TriggerDefinition entry;
                string? eventName;

                if (parser.Accept<Scalar>(out _))
                {
                    var item = parser.Consume<Scalar>();
                    eventName = IsNullScalar(item) ? null : item.Value;
                    entry = eventName == null
                        ? EntryError("trigger entry must be an event name or a single-key map")
                        : ReadEntry(eventName);
                }
                else if (parser.Accept<MappingStart>(out _))
                {
                    (entry, eventName) = ReadMapEntry(parser);
                }
                else
                {
                    parser.SkipThisAndNestedEvents();
                    eventName = null;
                    entry = EntryError("trigger entry must be an event name or a single-key map");
                }

                if (eventName != null && !seen.Add(eventName))
                {
                    entry.AddError($"duplicate trigger event '{eventName}'");
                }

                triggers.Add(entry);
            }

            parser.Consume<SequenceEnd>();
            return triggers;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var triggers = value as List<TriggerDefinition> ?? [];

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));

            foreach (var trigger in triggers)
            {
                if (trigger.Errors.Count > 0)
                {
                    // Entries that failed validation are parse diagnostics, not configuration.
                    continue;
                }

                var name = NamesByEvent[trigger.Event];
                if (!HasOptions(trigger))
                {
                    emitter.Emit(new Scalar(name));
                    continue;
                }

                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
                emitter.Emit(new Scalar(name));

                switch (trigger.Event)
                {
                    case TriggerEvent.PrePush:
                        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
                        if (trigger.Branches.Count > 0)
                        {
                            EmitStringList(emitter, "branches", trigger.Branches);
                        }

                        if (trigger.Tags.Count > 0)
                        {
                            EmitStringList(emitter, "tags", trigger.Tags);
                        }

                        emitter.Emit(new MappingEnd());
                        break;
                    case TriggerEvent.PostCheckout:
                        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, MappingStyle.Block));
                        emitter.Emit(new Scalar("clone"));
                        emitter.Emit(new Scalar(trigger.IncludeClone ? "true" : "false"));
                        emitter.Emit(new MappingEnd());
                        break;
                    case TriggerEvent.Schedule:
                        emitter.Emit(StringScalar(trigger.Cron ?? string.Empty));
                        break;
                    case TriggerEvent.Watch:
                        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
                        foreach (var path in trigger.WatchPaths)
                        {
                            emitter.Emit(StringScalar(path));
                        }

                        emitter.Emit(new SequenceEnd());
                        break;
                }

                emitter.Emit(new MappingEnd());
            }

            emitter.Emit(new SequenceEnd());
        }

        private static bool HasOptions(TriggerDefinition trigger) => trigger.Event switch
        {
            TriggerEvent.PrePush => trigger.Branches.Count > 0 || trigger.Tags.Count > 0,
            TriggerEvent.PostCheckout => trigger.IncludeClone,
            TriggerEvent.Schedule => !string.IsNullOrEmpty(trigger.Cron),
            TriggerEvent.Watch => trigger.WatchPaths.Count > 0,
            _ => false,
        };

        private static TriggerDefinition ReadEntry(string name)
        {
            if (EventsByName.TryGetValue(name, out var triggerEvent))
            {
                return new TriggerDefinition { Event = triggerEvent };
            }

            // Unrecognized names still produce an entry so the error shows up in status output.
            var entry = new TriggerDefinition();
            entry.AddError($"unknown trigger event '{name}'");
            return entry;
        }

        private static TriggerDefinition EntryError(string message)
        {
            var entry = new TriggerDefinition();
            entry.AddError(message);
            return entry;
        }

        private static (TriggerDefinition Entry, string EventName) ReadMapEntry(IParser parser)
        {
            parser.Consume<MappingStart>();

            var name = parser.Consume<Scalar>().Value;
            TriggerDefinition entry;
            if (EventsByName.TryGetValue(name, out var triggerEvent))
            {
                entry = new TriggerDefinition { Event = triggerEvent };
                ReadOptions(parser, entry, triggerEvent, name);
            }
            else
            {
                entry = new TriggerDefinition();
                entry.AddError($"unknown trigger event '{name}'");
                parser.SkipThisAndNestedEvents();
            }

            var hasExtraKeys = false;
            while (!parser.Accept<MappingEnd>(out _))
            {
                parser.Consume<Scalar>();
                parser.SkipThisAndNestedEvents();
                hasExtraKeys = true;
            }

            if (hasExtraKeys)
            {
                entry.AddError("trigger entry must have a single event key");
            }

            parser.Consume<MappingEnd>();
            return (entry, name);
        }

        private static void ReadOptions(IParser parser, TriggerDefinition entry, TriggerEvent triggerEvent, string name)
        {
            switch (triggerEvent)
            {
                case TriggerEvent.PrePush:
                    ReadPushOptions(parser, entry);
                    break;
                case TriggerEvent.PostCheckout:
                    ReadPostCheckoutOptions(parser, entry);
                    break;
                case TriggerEvent.Schedule:
                    ReadSchedule(parser, entry);
                    break;
                case TriggerEvent.Watch:
                    ReadWatch(parser, entry);
                    break;
                default:
                    ReadUnsupportedOptions(parser, entry, name);
                    break;
            }
        }

        private static void ReadPushOptions(IParser parser, TriggerDefinition entry)
        {
            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (!IsNullScalar(scalar) && scalar.Value.Length > 0)
                {
                    entry.AddError("trigger 'push' options must be a mapping with 'branches' or 'tags'");
                }

                return;
            }

            if (parser.Accept<SequenceStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                entry.AddError("trigger 'push' options must be a mapping with 'branches' or 'tags'");
                return;
            }

            if (!parser.Accept<MappingStart>(out _))
            {
                // `- push:` with no value: subscribe without filters.
                return;
            }

            parser.Consume<MappingStart>();
            while (!parser.Accept<MappingEnd>(out _))
            {
                var option = parser.Consume<Scalar>().Value;
                switch (option.ToLowerInvariant())
                {
                    case "branches":
                        entry.Branches = ReadStringList(parser, entry, "push", option);
                        break;
                    case "tags":
                        entry.Tags = ReadStringList(parser, entry, "push", option);
                        break;
                    default:
                        entry.AddError($"unknown option '{option}' for trigger 'push'");
                        parser.SkipThisAndNestedEvents();
                        break;
                }
            }

            parser.Consume<MappingEnd>();
        }

        /// <summary>
        /// Reads the only supported <c>post-checkout</c> option, <c>clone</c> (a boolean opting the
        /// pipeline into the initial clone checkout). The map form is allowed so this event can carry
        /// options, but any other key is a validation error.
        /// </summary>
        private static void ReadPostCheckoutOptions(IParser parser, TriggerDefinition entry)
        {
            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (!IsNullScalar(scalar) && scalar.Value.Length > 0)
                {
                    entry.AddError("trigger 'post-checkout' options must be a mapping with 'clone'");
                }

                return;
            }

            if (parser.Accept<SequenceStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                entry.AddError("trigger 'post-checkout' options must be a mapping with 'clone'");
                return;
            }

            if (!parser.Accept<MappingStart>(out _))
            {
                // `- post-checkout:` with no value: subscribe without options.
                return;
            }

            parser.Consume<MappingStart>();
            while (!parser.Accept<MappingEnd>(out _))
            {
                var option = parser.Consume<Scalar>().Value;
                switch (option.ToLowerInvariant())
                {
                    case "clone":
                        entry.IncludeClone = ReadBoolean(parser, entry, "post-checkout", option, defaultValue: true);
                        break;
                    default:
                        entry.AddError($"unknown option '{option}' for trigger 'post-checkout'");
                        parser.SkipThisAndNestedEvents();
                        break;
                }
            }

            parser.Consume<MappingEnd>();
        }

        /// <summary>
        /// Reads a boolean option value. A bare key (<c>clone:</c>) defaults to <paramref name="defaultValue"/>;
        /// recognized spellings are <c>true/false</c>, <c>yes/no</c>, <c>on/off</c> and <c>1/0</c>.
        /// </summary>
        private static bool ReadBoolean(
            IParser parser, TriggerDefinition entry, string keyword, string option, bool defaultValue)
        {
            if (!parser.Accept<Scalar>(out _))
            {
                parser.SkipThisAndNestedEvents();
                entry.AddError($"option '{option}' of trigger '{keyword}' must be a boolean");
                return defaultValue;
            }

            var scalar = parser.Consume<Scalar>();
            if (IsNullScalar(scalar))
            {
                return defaultValue;
            }

            switch (scalar.Value.ToLowerInvariant())
            {
                case "true" or "yes" or "on" or "1":
                    return true;
                case "false" or "no" or "off" or "0":
                    return false;
                default:
                    entry.AddError($"option '{option}' of trigger '{keyword}' must be a boolean");
                    return defaultValue;
            }
        }

        private static void ReadSchedule(IParser parser, TriggerDefinition entry)
        {
            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (IsNullScalar(scalar) || scalar.Value.Length == 0)
                {
                    entry.AddError("schedule requires exactly one cron expression");
                }
                else
                {
                    entry.Cron = scalar.Value;
                }

                return;
            }

            if (parser.Accept<SequenceStart>(out _) || parser.Accept<MappingStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                entry.AddError("schedule requires exactly one cron expression (declare multiple schedules as separate trigger entries)");
                return;
            }

            entry.AddError("schedule requires exactly one cron expression");
        }

        private static void ReadWatch(IParser parser, TriggerDefinition entry)
        {
            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (IsNullScalar(scalar))
                {
                    entry.WatchPaths = ["**/*"];
                }
                else if (scalar.Value.Length == 0)
                {
                    entry.AddError("watch requires a glob or a non-empty list of globs");
                }
                else
                {
                    entry.WatchPaths = [scalar.Value];
                }

                return;
            }

            if (parser.Accept<SequenceStart>(out _))
            {
                parser.Consume<SequenceStart>();

                var paths = new List<string>();
                while (!parser.Accept<SequenceEnd>(out _))
                {
                    if (parser.Accept<Scalar>(out _))
                    {
                        var item = parser.Consume<Scalar>();
                        if (IsNullScalar(item) || item.Value.Length == 0)
                        {
                            entry.AddError("watch glob patterns must not be empty");
                        }
                        else
                        {
                            paths.Add(item.Value);
                        }
                    }
                    else
                    {
                        parser.SkipThisAndNestedEvents();
                        entry.AddError("watch requires a glob or a non-empty list of globs");
                    }
                }

                parser.Consume<SequenceEnd>();

                if (paths.Count > 0)
                {
                    entry.WatchPaths = paths;
                }
                else if (entry.Errors.Count == 0)
                {
                    // `- watch: []` defaults to every file.
                    entry.WatchPaths = ["**/*"];
                }

                return;
            }

            if (parser.Accept<MappingStart>(out _))
            {
                parser.Consume<MappingStart>();
                if (parser.Accept<MappingEnd>(out _))
                {
                    // `- watch: {}` defaults to every file.
                    parser.Consume<MappingEnd>();
                    entry.WatchPaths = ["**/*"];
                    return;
                }

                while (!parser.Accept<MappingEnd>(out _))
                {
                    parser.Consume<Scalar>();
                    parser.SkipThisAndNestedEvents();
                }

                parser.Consume<MappingEnd>();
                entry.AddError("watch does not accept options; provide a glob or a list of globs");
                return;
            }

            // `- watch:` with no value defaults to every file.
            entry.WatchPaths = ["**/*"];
        }

        private static void ReadUnsupportedOptions(IParser parser, TriggerDefinition entry, string name)
        {
            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (!IsNullScalar(scalar) && scalar.Value.Length > 0)
                {
                    entry.AddError($"trigger '{name}' does not accept options");
                }

                return;
            }

            if (parser.Accept<SequenceStart>(out _))
            {
                parser.Consume<SequenceStart>();
                if (parser.Accept<SequenceEnd>(out _))
                {
                    parser.Consume<SequenceEnd>();
                    return;
                }

                while (!parser.Accept<SequenceEnd>(out _))
                {
                    parser.SkipThisAndNestedEvents();
                }

                parser.Consume<SequenceEnd>();
                entry.AddError($"trigger '{name}' does not accept options");
                return;
            }

            if (parser.Accept<MappingStart>(out _))
            {
                parser.Consume<MappingStart>();
                if (parser.Accept<MappingEnd>(out _))
                {
                    parser.Consume<MappingEnd>();
                    return;
                }

                while (!parser.Accept<MappingEnd>(out _))
                {
                    parser.Consume<Scalar>();
                    parser.SkipThisAndNestedEvents();
                }

                parser.Consume<MappingEnd>();
                entry.AddError($"trigger '{name}' does not accept options");
            }
        }

        private static IReadOnlyList<string> ReadStringList(
            IParser parser, TriggerDefinition entry, string keyword, string option)
        {
            if (parser.Accept<SequenceStart>(out _))
            {
                parser.Consume<SequenceStart>();

                var values = new List<string>();
                while (!parser.Accept<SequenceEnd>(out _))
                {
                    if (parser.Accept<Scalar>(out _))
                    {
                        values.Add(parser.Consume<Scalar>().Value);
                    }
                    else
                    {
                        parser.SkipThisAndNestedEvents();
                        entry.AddError($"option '{option}' of trigger '{keyword}' must be a list of strings");
                    }
                }

                parser.Consume<SequenceEnd>();
                return values;
            }

            if (parser.Accept<Scalar>(out _))
            {
                var scalar = parser.Consume<Scalar>();
                if (IsNullScalar(scalar))
                {
                    return [];
                }

                return [scalar.Value];
            }

            if (parser.Accept<MappingStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                entry.AddError($"option '{option}' of trigger '{keyword}' must be a list of strings");
                return [];
            }

            return [];
        }

        private static void EmitStringList(IEmitter emitter, string name, IReadOnlyList<string> values)
        {
            emitter.Emit(new Scalar(name));
            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Block));
            foreach (var value in values)
            {
                emitter.Emit(StringScalar(value));
            }

            emitter.Emit(new SequenceEnd());
        }

        private static Scalar StringScalar(string value) =>
            NeedsQuoting(value)
                ? new Scalar(AnchorName.Empty, TagName.Empty, value, ScalarStyle.DoubleQuoted, isPlainImplicit: false, isQuotedImplicit: true)
                : new Scalar(value);

        // Conservative plain-scalar safety check. Globs such as `**/*` start with an indicator and
        // must be quoted or the re-parse would read them as an alias.
        private static bool NeedsQuoting(string value)
        {
            if (value.Length == 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            {
                return true;
            }

            if (value[0] is '-' or '?' or ':' or ',' or '[' or ']' or '{' or '}' or '#'
                or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`')
            {
                return true;
            }

            if (value.Contains(": ") || value.Contains(" #") || value.EndsWith(':'))
            {
                return true;
            }

            if (value.IndexOfAny(['\r', '\n', '\t', '\0']) >= 0)
            {
                return true;
            }

            return value.ToLowerInvariant() switch
            {
                "true" or "false" or "yes" or "no" or "y" or "n" or "on" or "off" or "null" or "~" => true,
                _ => false,
            };
        }
    }

    /// <summary>
    /// Reads and writes the <c>notify:</c> scalar (<c>never</c>, <c>on-failure</c>, <c>always</c>,
    /// case-insensitive). An unrecognized value falls back to <see cref="NotificationLevel.OnFailure"/>
    /// so a malformed <c>notify</c> never fails the pipeline parse.
    /// </summary>
    private sealed class NotificationLevelYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(NotificationLevel);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (!parser.Accept<Scalar>(out _))
            {
                parser.SkipThisAndNestedEvents();
                return NotificationLevel.OnFailure;
            }

            var scalar = parser.Consume<Scalar>();
            if (IsNullScalar(scalar))
            {
                return NotificationLevel.OnFailure;
            }

            return scalar.Value.ToLowerInvariant() switch
            {
                "never" => NotificationLevel.Never,
                "always" => NotificationLevel.Always,
                "on-failure" => NotificationLevel.OnFailure,
                _ => NotificationLevel.OnFailure,
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            var level = value is NotificationLevel notification ? notification : NotificationLevel.OnFailure;
            emitter.Emit(new Scalar(level switch
            {
                NotificationLevel.Never => "never",
                NotificationLevel.Always => "always",
                _ => "on-failure",
            }));
        }
    }

    private static bool IsNullScalar(Scalar scalar) =>
        (scalar.Style == ScalarStyle.Plain || scalar.Style == ScalarStyle.Any)
        && (scalar.Value.Length == 0
            || scalar.Value == "~"
            || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase));
}
