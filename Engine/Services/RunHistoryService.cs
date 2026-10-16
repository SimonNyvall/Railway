using Microsoft.EntityFrameworkCore;
using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Services;

public class RunHistoryService : IRunHistoryService
{
    private const int OutputTruncationLength = 4000;

    private readonly RailwayDbContext _dbContext;

    public RunHistoryService(RailwayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Run>> ListRunsAsync(string startPath, int limit = 50, CancellationToken ct = default)
    {
        if (limit < 1)
        {
            limit = 50;
        }

        var repository = await FindRepositoryAsync(startPath, ct);
        if (repository == null)
        {
            return Array.Empty<Run>();
        }

        // SQLite cannot ORDER BY DateTimeOffset, so order by the auto-increment
        // Id (insertion order == run order) to get the latest N runs.
        return await _dbContext.Runs
            .Where(r => r.RepositoryId == repository.Id)
            .OrderByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Run> GetRunAsync(string startPath, int runId, CancellationToken ct = default)
    {
        var repository = await FindRepositoryAsync(startPath, ct);

        var run = repository == null
            ? null
            : await _dbContext.Runs
                .Where(r => r.Id == runId && r.RepositoryId == repository.Id)
                .Include(r => r.Jobs)
                    .ThenInclude(j => j.Steps)
                .FirstOrDefaultAsync(ct);

        if (run == null)
        {
            throw new ArgumentException($"Run {runId} was not found.", nameof(runId));
        }

        foreach (var job in run.Jobs)
        {
            foreach (var step in job.Steps)
            {
                if (step.Output.Length > OutputTruncationLength)
                {
                    step.Output = step.Output[^OutputTruncationLength..];
                }
            }
        }

        return run;
    }

    private async Task<Repository?> FindRepositoryAsync(string startPath, CancellationToken ct)
    {
        _ = RepositoryHelper.GetRailwayPathOrThrow(startPath);
        var repoPath = RepositoryHelper.GetRootRepositoryPath(startPath)
            ?? throw new NotARailwayRepositoryException();

        return await _dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Path == repoPath, ct);
    }
}