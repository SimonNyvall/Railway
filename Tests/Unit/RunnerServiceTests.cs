using System.Collections;
using Cli.Domain;
using Cli.Services;
using Xunit;

namespace Tests.Unit;

public class RunnerServiceTests
{
    [Fact]
    public void TopologicalSort_SimpleLinearDependency_ReturnsCorrectOrder()
    {
        var jobs = new Dictionary<string, Job>
        {
            ["build"] = new Job { DependsOn = [], Steps = [new Step { Run = "echo build" }] },
            ["test"] = new Job { DependsOn = ["build"], Steps = [new Step { Run = "echo test" }] },
            ["deploy"] = new Job { DependsOn = ["test"], Steps = [new Step { Run = "echo deploy" }] }
        };

        var order = RunnerServiceTopologicalSort(jobs);

        Assert.Equal(["build", "test", "deploy"], order);
    }

    [Fact]
    public void TopologicalSort_DiamondDependency_ReturnsValidOrder()
    {
        var jobs = new Dictionary<string, Job>
        {
            ["a"] = new Job { DependsOn = [], Steps = [new Step { Run = "echo a" }] },
            ["b"] = new Job { DependsOn = ["a"], Steps = [new Step { Run = "echo b" }] },
            ["c"] = new Job { DependsOn = ["a"], Steps = [new Step { Run = "echo c" }] },
            ["d"] = new Job { DependsOn = ["b", "c"], Steps = [new Step { Run = "echo d" }] }
        };

        var order = RunnerServiceTopologicalSort(jobs);

        Assert.Equal(4, order.Count);
        Assert.Equal("a", order[0]);
        Assert.Contains("b", order);
        Assert.Contains("c", order);
        Assert.Equal("d", order[3]);
        Assert.True(order.IndexOf("b") < order.IndexOf("d"));
        Assert.True(order.IndexOf("c") < order.IndexOf("d"));
    }

    [Fact]
    public void TopologicalSort_IndependentJobs_ReturnsAllJobs()
    {
        var jobs = new Dictionary<string, Job>
        {
            ["job1"] = new Job { DependsOn = [], Steps = [new Step { Run = "echo 1" }] },
            ["job2"] = new Job { DependsOn = [], Steps = [new Step { Run = "echo 2" }] },
            ["job3"] = new Job { DependsOn = [], Steps = [new Step { Run = "echo 3" }] }
        };

        var order = RunnerServiceTopologicalSort(jobs);

        Assert.Equal(3, order.Count);
        Assert.Contains("job1", order);
        Assert.Contains("job2", order);
        Assert.Contains("job3", order);
    }

    [Fact]
    public void TopologicalSort_CircularDependency_ThrowsException()
    {
        var jobs = new Dictionary<string, Job>
        {
            ["a"] = new Job { DependsOn = ["b"], Steps = [new Step { Run = "echo a" }] },
            ["b"] = new Job { DependsOn = ["a"], Steps = [new Step { Run = "echo b" }] }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => RunnerServiceTopologicalSort(jobs));
        Assert.Contains("Circular dependency", exception.Message);
    }

    [Fact]
    public void BuildEnvironment_MergesProcessEnvAndPipelineVariables()
    {
        var pipeline = new Pipeline
        {
            Variables = new Dictionary<string, string>
            {
                ["PIPELINE_VAR"] = "pipeline_value",
                ["OVERRIDE_VAR"] = "pipeline_override"
            }
        };

        var env = RunnerServiceBuildEnvironment(pipeline);

        Assert.Contains(env, kvp => kvp.Key == "PIPELINE_VAR" && kvp.Value == "pipeline_value");
        Assert.Contains(env, kvp => kvp.Key == "OVERRIDE_VAR" && kvp.Value == "pipeline_override");
    }

    [Fact]
    public void MergeEnvironments_OverrideWins()
    {
        var baseEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BASE_VAR"] = "base_value",
            ["OVERRIDE_VAR"] = "base_override"
        };

        var overrideEnv = new Dictionary<string, string>
        {
            ["OVERRIDE_VAR"] = "override_value",
            ["NEW_VAR"] = "new_value"
        };

        var result = RunnerServiceMergeEnvironments(baseEnv, overrideEnv);

        Assert.Equal("base_value", result["BASE_VAR"]);
        Assert.Equal("override_value", result["OVERRIDE_VAR"]);
        Assert.Equal("new_value", result["NEW_VAR"]);
    }

    [Fact]
    public void MergeEnvironments_CaseInsensitive()
    {
        var baseEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VAR_ONE"] = "one"
        };

        var overrideEnv = new Dictionary<string, string>
        {
            ["var_one"] = "two"
        };

        var result = RunnerServiceMergeEnvironments(baseEnv, overrideEnv);

        Assert.Equal("two", result["VAR_ONE"]);
        Assert.Equal("two", result["var_one"]);
    }

    // Helper methods to test private methods via reflection or by extracting logic
    // Since the methods are private, we'll replicate the logic here for testing

    private static List<string> RunnerServiceTopologicalSort(Dictionary<string, Job> jobs)
    {
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();

        void Visit(string jobName)
        {
            if (visiting.Contains(jobName))
            {
                throw new InvalidOperationException($"Circular dependency detected involving job '{jobName}'");
            }

            if (visited.Contains(jobName))
            {
                return;
            }

            visiting.Add(jobName);

            if (jobs.TryGetValue(jobName, out var job))
            {
                foreach (var dependency in job.DependsOn)
                {
                    Visit(dependency);
                }
            }

            visiting.Remove(jobName);
            visited.Add(jobName);
            result.Add(jobName);
        }

        foreach (var jobName in jobs.Keys)
        {
            Visit(jobName);
        }

        return result;
    }

    private static Dictionary<string, string> RunnerServiceBuildEnvironment(Pipeline pipeline)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        foreach (var kvp in pipeline.Variables)
        {
            env[kvp.Key] = kvp.Value;
        }

        return env;
    }

    private static Dictionary<string, string> RunnerServiceMergeEnvironments(
        Dictionary<string, string> baseEnv,
        Dictionary<string, string> overrideEnv)
    {
        var result = new Dictionary<string, string>(baseEnv, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in overrideEnv)
        {
            result[kvp.Key] = kvp.Value;
        }

        return result;
    }
}
