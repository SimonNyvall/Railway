using Cli.Domain;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cli.Services;

public class DashboardService : IDashboardService
{
    private readonly RailwayDbContext _dbContext;

    public DashboardService()
    {
        _dbContext = LifetimeService.Get<RailwayDbContext>();
    }

    public DashboardService(RailwayDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardView> GetView()
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow();
        var repoPath = RepositoryHelper.GetRootRepositoryPath()
            ?? throw new NotARailwayRepositoryException();

        var repoName = new DirectoryInfo(repoPath).Name;

        var pipelines = await PipelineDiscoveryService.DiscoverStatic(railwayPath);

        var repository = await _dbContext.Repositories
            .FirstOrDefaultAsync(r => r.Path == repoPath);

        var recentRuns = new List<Run>();
        if (repository != null)
        {
            recentRuns = await _dbContext.Runs
                .Where(r => r.RepositoryId == repository.Id)
                .Include(r => r.Jobs)
                    .ThenInclude(j => j.Steps)
                .ToListAsync();
            
            recentRuns = recentRuns
                .OrderByDescending(r => r.StartedAt)
                .Take(10)
                .ToList();
        }

        return new DashboardView
        {
            RepositoryName = repoName,
            Pipelines = pipelines,
            RecentRuns = recentRuns
        };
    }
}
