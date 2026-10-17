using Railway.Engine.Domain;
using Railway.Engine.Helpers;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

/// <summary>
/// Parsing and round-trip tests for the pipeline hooks <c>triggers:</c> and <c>notify:</c> keys
/// (task 1.1): scalar and map trigger forms, duplicate/invalid entries collected as validation
/// errors instead of parse failures, and legacy pipelines that declare neither key.
/// </summary>
public class TriggerDefinitionParsingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    /// <inheritdoc/>
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Parse_ThenWrite_ThenParse_RoundTripsAllTriggerForms()
    {
        const string yaml = """
            name: Hooks
            notify: always
            triggers:
              - post-commit
              - post-merge
              - post-checkout
              - push:
                  branches: [main, "release/*"]
                  tags: ["v*"]
              - schedule: "0 3 * * *"
              - watch: ["src/**/*.cs", "Tests/**/*.cs"]
            jobs:
              build:
                steps:
                  - run: dotnet build
            """;

        var first = await ParseYamlAsync(yaml);

        Assert.Equal(NotificationLevel.Always, first.Notify);
        Assert.Equal(6, first.Triggers.Count);
        Assert.All(first.Triggers, trigger => Assert.Empty(trigger.Errors));

        Assert.Equal(TriggerEvent.PostCommit, first.Triggers[0].Event);
        Assert.Equal(TriggerEvent.PostMerge, first.Triggers[1].Event);
        Assert.Equal(TriggerEvent.PostCheckout, first.Triggers[2].Event);

        var push = first.Triggers[3];
        Assert.Equal(TriggerEvent.PrePush, push.Event);
        Assert.Equal(new[] { "main", "release/*" }, push.Branches);
        Assert.Equal(new[] { "v*" }, push.Tags);

        var schedule = first.Triggers[4];
        Assert.Equal(TriggerEvent.Schedule, schedule.Event);
        Assert.Equal("0 3 * * *", schedule.Cron);

        var watch = first.Triggers[5];
        Assert.Equal(TriggerEvent.Watch, watch.Event);
        Assert.Equal(new[] { "src/**/*.cs", "Tests/**/*.cs" }, watch.WatchPaths);

        var path = _temp.File("roundtrip.yaml");
        await PipelineHelper.Write(first, path, TestContext.Current.CancellationToken);
        var second = await PipelineHelper.Parse(path);

        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Notify, second.Notify);
        AssertTriggersEqual(first.Triggers, second.Triggers);
    }

    [Fact]
    public async Task Parse_LegacyPipelineWithoutTriggersOrNotify_ParsesAndWritesAsBefore()
    {
        const string yaml = """
            name: Legacy
            jobs:
              build:
                steps:
                  - run: dotnet build
            """;

        var pipeline = await ParseYamlAsync(yaml);

        Assert.Equal("Legacy", pipeline.Name);
        Assert.Empty(pipeline.Triggers);
        Assert.Equal(NotificationLevel.OnFailure, pipeline.Notify);

        var path = _temp.File("legacy-out.yaml");
        await PipelineHelper.Write(pipeline, path, TestContext.Current.CancellationToken);
        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("triggers:", written);
        Assert.DoesNotContain("notify:", written);
    }

    [Fact]
    public async Task Parse_DuplicateTriggerEvents_RecordsErrorWithoutFailing()
    {
        const string yaml = """
            name: Dup
            triggers:
              - push:
                  branches: [main]
              - push:
                  tags: ["v*"]
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        Assert.Equal(2, pipeline.Triggers.Count);
        Assert.Empty(pipeline.Triggers[0].Errors);
        var duplicate = Assert.Single(pipeline.Triggers[1].Errors);
        Assert.Contains("duplicate trigger event", duplicate);
    }

    [Fact]
    public async Task Parse_OptionsOnUnsupportedEvent_RecordsErrorWithoutFailing()
    {
        const string yaml = """
            name: Bad
            triggers:
              - post-commit:
                  branches: [main]
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Equal(TriggerEvent.PostCommit, trigger.Event);
        Assert.Contains(trigger.Errors, error => error.Contains("does not accept options"));
    }

    [Fact]
    public async Task Parse_PostCheckoutCloneOptIn_RoundTrips()
    {
        const string yaml = """
            name: Checkout
            triggers:
              - post-checkout:
                  clone: true
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Equal(TriggerEvent.PostCheckout, trigger.Event);
        Assert.Empty(trigger.Errors);
        Assert.True(trigger.IncludeClone);

        var path = _temp.File("clone.yaml");
        await PipelineHelper.Write(pipeline, path, TestContext.Current.CancellationToken);
        var written = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n");

        Assert.Contains("- post-checkout:\n", written);
        Assert.Contains("clone: true", written);

        var reparsed = await PipelineHelper.Parse(path);
        Assert.True(Assert.Single(reparsed.Triggers).IncludeClone);
        AssertTriggersEqual(pipeline.Triggers, reparsed.Triggers);
    }

    [Fact]
    public async Task Parse_PostCheckoutBareScalar_ExcludesCloneByDefault()
    {
        const string yaml = """
            name: Checkout
            triggers:
              - post-checkout
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Equal(TriggerEvent.PostCheckout, trigger.Event);
        Assert.Empty(trigger.Errors);
        Assert.False(trigger.IncludeClone);

        // A bare scalar stays a bare scalar on the round-trip.
        var path = _temp.File("bare-clone.yaml");
        await PipelineHelper.Write(pipeline, path, TestContext.Current.CancellationToken);
        var written = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n");
        Assert.Contains("- post-checkout\n", written);
        Assert.DoesNotContain("clone", written);
    }

    [Fact]
    public async Task Parse_PostCheckoutUnknownOption_RecordsErrorWithoutFailing()
    {
        const string yaml = """
            name: Checkout
            triggers:
              - post-checkout:
                  branches: [main]
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Equal(TriggerEvent.PostCheckout, trigger.Event);
        Assert.False(trigger.IncludeClone);
        Assert.Contains(trigger.Errors, error => error.Contains("unknown option 'branches' for trigger 'post-checkout'"));
    }

    [Theory]
    [InlineData("post-commit")]
    [InlineData("post-merge")]
    public async Task Parse_OptionsOnPostCommitOrPostMerge_StillRecordsError(string eventName)
    {
        var yaml = $$"""
            name: Bad
            triggers:
              - {{eventName}}:
                  clone: true
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.False(trigger.IncludeClone);
        Assert.Contains(trigger.Errors, error => error.Contains("does not accept options"));
    }

    [Fact]
    public async Task Parse_UnknownTriggerEvent_RecordsErrorWithoutFailing()
    {
        const string yaml = """
            name: Unknown
            triggers:
              - on-tag
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Contains(trigger.Errors, error => error.Contains("unknown trigger event"));
    }

    [Fact]
    public async Task Parse_WatchWithoutGlobs_DefaultsToAllFiles()
    {
        const string yaml = """
            name: Watch
            triggers:
              - watch:
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        var trigger = Assert.Single(pipeline.Triggers);
        Assert.Equal(TriggerEvent.Watch, trigger.Event);
        Assert.Empty(trigger.Errors);
        Assert.Equal(new[] { "**/*" }, trigger.WatchPaths);
    }

    [Theory]
    [InlineData("never", NotificationLevel.Never)]
    [InlineData("on-failure", NotificationLevel.OnFailure)]
    [InlineData("always", NotificationLevel.Always)]
    [InlineData("ALWAYS", NotificationLevel.Always)]
    public async Task Parse_Notify_MapsLevelsCaseInsensitively(string value, NotificationLevel expected)
    {
        var pipeline = await ParseYamlAsync($$"""
            name: Notify
            notify: {{value}}
            jobs: {}
            """);

        Assert.Equal(expected, pipeline.Notify);
    }

    [Fact]
    public async Task Parse_InvalidNotifyValue_FallsBackToDefaultWithoutFailing()
    {
        const string yaml = """
            name: Notify
            notify: sometimes
            jobs: {}
            """;

        var pipeline = await ParseYamlAsync(yaml);

        Assert.Equal(NotificationLevel.OnFailure, pipeline.Notify);
    }

    private async Task<Pipeline> ParseYamlAsync(string yaml)
    {
        var path = _temp.File($"pipeline-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, yaml, TestContext.Current.CancellationToken);
        return await PipelineHelper.Parse(path);
    }

    private static void AssertTriggersEqual(
        IReadOnlyList<TriggerDefinition> expected, IReadOnlyList<TriggerDefinition> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Event, actual[i].Event);
            Assert.Equal(expected[i].Branches, actual[i].Branches);
            Assert.Equal(expected[i].Tags, actual[i].Tags);
            Assert.Equal(expected[i].WatchPaths, actual[i].WatchPaths);
            Assert.Equal(expected[i].Cron, actual[i].Cron);
            Assert.Equal(expected[i].IncludeClone, actual[i].IncludeClone);
            Assert.Equal(expected[i].Errors, actual[i].Errors);
        }
    }
}
