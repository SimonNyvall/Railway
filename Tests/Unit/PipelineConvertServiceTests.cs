using Railway.Engine.Domain;
using Railway.Engine.Services;

using Xunit;

namespace Tests.Unit;

public class PipelineConvertServiceTests
{
    private readonly PipelineConvertService _service = new();

    // ------------------------------------------------------------------
    // DetectFormat
    // ------------------------------------------------------------------

    [Fact]
    public void DetectFormat_GitHubWorkflowPath_ReturnsGitHub()
    {
        var source = WriteSource("nothing: to see here", Path.Combine(".github", "workflows", "ci.yml"));

        try
        {
            Assert.Equal(SourceFormat.GitHub, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_GitHubContent_ReturnsGitHub()
    {
        var source = WriteSource("""
            name: CI
            on: [push]
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
            """);

        try
        {
            Assert.Equal(SourceFormat.GitHub, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_GitLabFileName_ReturnsGitLab()
    {
        // Path heuristics win before content is even read.
        var source = WriteSource("unrelated: content", ".gitlab-ci.yml");

        try
        {
            Assert.Equal(SourceFormat.GitLab, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_GitLabContent_ReturnsGitLab()
    {
        var source = WriteSource("""
            stages: [build, test]
            build-job:
              stage: build
              image: mcr.microsoft.com/dotnet/sdk:10.0
              script:
                - dotnet build
            test-job:
              stage: test
              script:
                - dotnet test
            """);

        try
        {
            Assert.Equal(SourceFormat.GitLab, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_AzureFileName_ReturnsAzure()
    {
        var source = WriteSource("unrelated: content", "azure-pipelines.yml");

        try
        {
            Assert.Equal(SourceFormat.Azure, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_AzureStageBlocksContent_ReturnsAzure()
    {
        var source = WriteSource("""
            trigger:
              - main
            stages:
              - stage: Build
                jobs:
                  - job: BuildJob
                    steps:
                      - script: dotnet build
            """);

        try
        {
            Assert.Equal(SourceFormat.Azure, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_AzurePoolContent_ReturnsAzure()
    {
        var source = WriteSource("""
            jobs:
              - job: BuildJob
                pool:
                  vmImage: ubuntu-latest
                steps:
                  - script: dotnet build
            """);

        try
        {
            Assert.Equal(SourceFormat.Azure, _service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_AmbiguousContent_ReturnsNull()
    {
        var source = WriteSource("""
            on: [push]
            stages:
              - build
            jobs:
              build:
                runs-on: ubuntu-latest
                script:
                  - echo hi
            """);

        try
        {
            Assert.Null(_service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_UnrelatedYaml_ReturnsNull()
    {
        var source = WriteSource("""
            services:
              web:
                image: nginx
            """);

        try
        {
            Assert.Null(_service.DetectFormat(source));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void DetectFormat_MissingFile_ReturnsNull()
    {
        Assert.Null(_service.DetectFormat(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yaml")));
    }

    // ------------------------------------------------------------------
    // GitHub Actions conversion
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_GitHub_MapsJobsNeedsStepsTimeoutAndEnv()
    {
        var source = WriteSource("""
            name: CI
            on: [push]
            env:
              GLOBAL_FLAG: "true"
            jobs:
              build:
                runs-on: ubuntu-latest
                timeout-minutes: 30
                env:
                  DOTNET_NOLOGO: "true"
                steps:
                  - name: Checkout
                    uses: actions/checkout@v4
                  - name: Build
                    run: dotnet build
              test:
                needs: [build]
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet test
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);

            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.Pipeline);

            var pipeline = outcome.Pipeline!;
            Assert.Equal("CI", pipeline.Name);
            Assert.Equal("true", pipeline.Variables["GLOBAL_FLAG"]);
            Assert.Equal(2, pipeline.Jobs.Count);

            var build = pipeline.Jobs["build"];
            Assert.Equal(TimeSpan.FromMinutes(30), build.Timeout);
            Assert.Equal("true", build.Env["DOTNET_NOLOGO"]);
            Assert.Empty(build.DependsOn);
            var buildStep = Assert.Single(build.Steps);
            Assert.Equal("Build", buildStep.Name);
            Assert.Equal("dotnet build", buildStep.Run);

            var test = pipeline.Jobs["test"];
            Assert.Single(test.DependsOn);
            Assert.Equal("build", test.DependsOn[0]);
            var testStep = Assert.Single(test.Steps);
            Assert.Null(testStep.Name);
            Assert.Equal("dotnet test", testStep.Run);
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_GitHub_UnmappableConstructsProduceJobContextWarnings()
    {
        var source = WriteSource("""
            on: [push]
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - name: Checkout
                    uses: actions/checkout@v4
                    with:
                      submodules: true
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);

            Assert.Contains(outcome.Warnings, w => w == "job 'build': 'runs-on' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w == "job 'build': step 'Checkout': 'uses' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w == "job 'build': step 'Checkout': 'with' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w == "'on' cannot be mapped");
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_GitHub_NotableUnmappableKeysEachProduceWarning()
    {
        var source = WriteSource("""
            on: [push]
            permissions:
              contents: read
            concurrency:
              group: build
            defaults:
              run:
                shell: bash
            jobs:
              build:
                runs-on: ubuntu-latest
                if: github.event_name == 'push'
                continue-on-error: true
                strategy:
                  matrix:
                    os: [ubuntu-latest, windows-latest]
                container: node:20
                services:
                  db:
                    image: postgres:16
                steps:
                  - run: echo hi
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);
            var expectedKeys = new[]
            {
                "on", "permissions", "concurrency", "defaults", "runs-on", "if",
                "continue-on-error", "strategy", "container", "services"
            };

            Assert.Null(outcome.Error);
            Assert.All(expectedKeys, key =>
                Assert.Contains(outcome.Warnings, w => w.Contains($"'{key}'", StringComparison.Ordinal)));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_GitHub_JobsWithoutConvertibleSteps_ReturnsError()
    {
        var source = WriteSource("""
            on: workflow_call
            jobs:
              call-workflow:
                uses: ./.github/workflows/reusable.yml
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);

            Assert.NotNull(outcome.Error);
            Assert.Null(outcome.Pipeline);
            Assert.Contains("no convertible jobs", outcome.Error);
            Assert.Contains(outcome.Warnings, w => w.Contains("job 'call-workflow'") && w.Contains("'uses'"));
        }
        finally
        {
            Cleanup(source);
        }
    }

    // ------------------------------------------------------------------
    // GitLab CI conversion
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_GitLab_MapsScriptListVariablesAndStageDependencies()
    {
        var source = WriteSource("""
            stages:
              - build
              - test
            variables:
              DOTNET_NOLOGO: "true"
            build:
              stage: build
              image: mcr.microsoft.com/dotnet/sdk:10.0
              script:
                - dotnet restore
                - dotnet build
            test:
              stage: test
              variables:
                TEST_ENV: "1"
              script:
                - dotnet test
            lint:
              script:
                - mylint
            """, ".gitlab-ci.yml");

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitLab);

            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.Pipeline);

            var pipeline = outcome.Pipeline!;
            Assert.Equal(3, pipeline.Jobs.Count);
            Assert.Equal("true", pipeline.Variables["DOTNET_NOLOGO"]);

            var build = pipeline.Jobs["build"];
            Assert.Empty(build.DependsOn);
            Assert.Equal(2, build.Steps.Count);
            Assert.Equal("dotnet restore", build.Steps[0].Run);
            Assert.Equal("dotnet build", build.Steps[1].Run);
            Assert.All(build.Steps, s => Assert.Null(s.Name));

            var test = pipeline.Jobs["test"];
            Assert.Single(test.DependsOn);
            Assert.Equal("build", test.DependsOn[0]);
            Assert.Equal("1", test.Env["TEST_ENV"]);
            Assert.Equal("dotnet test", Assert.Single(test.Steps).Run);

            // A job without an explicit stage defaults to GitLab's 'test' stage, so it depends on stage 'build'.
            var lint = pipeline.Jobs["lint"];
            Assert.Single(lint.DependsOn);
            Assert.Equal("build", lint.DependsOn[0]);

            Assert.Contains(outcome.Warnings, w => w == "job 'build': 'image' cannot be mapped");
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_GitLab_NoStages_JobsHaveNoDependenciesAndNonJobsWarn()
    {
        var source = WriteSource("""
            variables:
              A: "1"
            workflow:
              rules:
                - when: always
            build-job:
              script: echo build
              needs: [other-job]
            trigger-job:
              trigger:
                project: some/project
            """, ".gitlab-ci.yml");

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitLab);

            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.Pipeline);

            var pipeline = outcome.Pipeline!;
            var buildJob = Assert.Single(pipeline.Jobs.Values);
            Assert.Equal("echo build", Assert.Single(buildJob.Steps).Run);
            Assert.Empty(buildJob.DependsOn);

            // GitLab 'needs' is not mapped in v1 (stage order is used instead) and the stage-less
            // heuristic keeps the job dependency-free.
            Assert.Contains(outcome.Warnings, w => w == "job 'build-job': 'needs' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w == "'workflow' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w == "job 'trigger-job': no convertible steps found (job skipped)");
        }
        finally
        {
            Cleanup(source);
        }
    }

    // ------------------------------------------------------------------
    // Azure DevOps conversion
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_Azure_MapsStageDependenciesTimeoutStepsAndListVariables()
    {
        var source = WriteSource("""
            name: my-pipeline
            variables:
              - name: BUILD_CONFIG
                value: Release
            stages:
              - stage: Build
                jobs:
                  - job: BuildJob
                    timeoutInMinutes: 45
                    pool:
                      vmImage: ubuntu-latest
                    steps:
                      - script: dotnet build
                        displayName: Build
                      - task: UseDotNet@2
              - stage: Test
                jobs:
                  - job: TestJob
                    steps:
                      - script: dotnet test
            """, "azure-pipelines.yml");

        try
        {
            var outcome = _service.Convert(source, SourceFormat.Azure);

            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.Pipeline);

            var pipeline = outcome.Pipeline!;
            Assert.Equal("my-pipeline", pipeline.Name);
            Assert.Equal("Release", pipeline.Variables["BUILD_CONFIG"]);
            Assert.Equal(2, pipeline.Jobs.Count);

            var buildJob = pipeline.Jobs["BuildJob"];
            Assert.Equal(TimeSpan.FromMinutes(45), buildJob.Timeout);
            var buildStep = Assert.Single(buildJob.Steps);
            Assert.Equal("Build", buildStep.Name);
            Assert.Equal("dotnet build", buildStep.Run);

            var testJob = pipeline.Jobs["TestJob"];
            Assert.Single(testJob.DependsOn);
            Assert.Equal("BuildJob", testJob.DependsOn[0]);
            Assert.Equal("dotnet test", Assert.Single(testJob.Steps).Run);

            Assert.Contains(outcome.Warnings, w => w == "job 'BuildJob': 'pool' cannot be mapped");
            Assert.Contains(outcome.Warnings, w => w.Contains("job 'BuildJob'") && w.Contains("'task'", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_Azure_MapsMappingSyntaxVariablesAndWarnsOnJobEntriesWithoutJobKey()
    {
        var source = WriteSource("""
            variables:
              BUILD_CONFIG: Debug
            stages:
              - stage: Lint
                jobs:
                  - script: mylint
              - stage: Build
                jobs:
                  - job: BuildJob
                    variables:
                      JOB_FLAG: "1"
                    steps:
                      - script: dotnet build
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.Azure);

            Assert.Null(outcome.Error);
            Assert.NotNull(outcome.Pipeline);

            var pipeline = outcome.Pipeline!;
            Assert.Equal("Debug", pipeline.Variables["BUILD_CONFIG"]);

            var buildJob = Assert.Single(pipeline.Jobs.Values);
            Assert.Equal("1", buildJob.Env["JOB_FLAG"]);
            // The previous stage had no convertible job, so the heuristic yields no dependency.
            Assert.Empty(buildJob.DependsOn);

            Assert.Contains(outcome.Warnings,
                w => w == "stage 'Lint': jobs entry without a 'job' key cannot be converted");
        }
        finally
        {
            Cleanup(source);
        }
    }

    // ------------------------------------------------------------------
    // Unconvertible input
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_NonCiYaml_ReturnsError()
    {
        var source = WriteSource("""
            server:
              port: 8080
            services:
              db:
                image: postgres
            """);

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);

            Assert.NotNull(outcome.Error);
            Assert.Null(outcome.Pipeline);
            Assert.Contains("not a recognizable GitHub Actions workflow", outcome.Error);
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_ScalarDocument_ReturnsError()
    {
        var source = WriteSource("just a plain string");

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitLab);

            Assert.NotNull(outcome.Error);
            Assert.Null(outcome.Pipeline);
            Assert.Contains("root must be a mapping", outcome.Error);
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_InvalidYaml_ReturnsError()
    {
        var source = WriteSource("foo: \"unterminated");

        try
        {
            var outcome = _service.Convert(source, SourceFormat.GitHub);

            Assert.NotNull(outcome.Error);
            Assert.Null(outcome.Pipeline);
            Assert.Contains("not valid YAML", outcome.Error);
        }
        finally
        {
            Cleanup(source);
        }
    }

    [Fact]
    public void Convert_MissingFile_ReturnsError()
    {
        var outcome = _service.Convert(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yaml"), SourceFormat.GitHub);

        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Pipeline);
        Assert.Contains("not found", outcome.Error);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string WriteSource(string content, string? relativePath = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"railway-convert-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, relativePath ?? "source.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Cleanup(string sourcePath)
    {
        // Temp sources live in a dedicated per-test directory (including any nested relative path).
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourcePath)!);
        while (directory is not null && !directory.Name.StartsWith("railway-convert-", StringComparison.Ordinal))
        {
            directory = directory.Parent;
        }

        if (directory is not null && directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }
}
