using Cli.Domain;
using Cli.Services.Interfaces;

namespace Cli.Services;

public class ValidateService : IValidateService
{
	public async Task<IEnumerable<string>> IsValid(Pipeline pipeline)
	{
		var errors = new List<ValidationError>();

		ValidateJobs(pipeline, errors);
		ValidateDependencies(pipeline, errors);
		ValidateSteps(pipeline, errors);

        return errors.Select(x => x.Message);
	}

	private static void ValidateJobs(Pipeline pipeline, List<ValidationError> errors)
	{
		if (pipeline.Jobs.Count == 0)
		{
			errors.Add(new("pipeline.jobs", "Pipeline must contain at least one job."));
		}
	}

	private static void ValidateDependencies(Pipeline pipeline, List<ValidationError> errors)
	{
		foreach (var (jobName, job) in pipeline.Jobs)
		{
			foreach (var dependency in job.DependsOn)
			{
				if (!pipeline.Jobs.ContainsKey(dependency))
				{
					errors.Add(new($"jobs.{jobName}.depends_on", $"Job '{dependency}' does not exist"));
				}
			}
		}
	}

	private static void ValidateSteps(Pipeline pipeline, List<ValidationError> errors)
	{
		foreach (var (jobName, job) in pipeline.Jobs)
		{
			if (job.Steps.Count == 0)
			{
				errors.Add(new($"jobs.{jobName}.steps", "Job must contain at leasst one step."));
			}

			foreach (var step in job.Steps)
			{
				if (string.IsNullOrWhiteSpace(step.Run))
				{
					errors.Add(new($"Jobs.{jobName}.steps", "Step must contain a 'run' command."));
				}
			}
		}
	}

	private record ValidationError(string Path, string Message);
}