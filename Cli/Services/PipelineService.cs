using Cli.Domain;
using Cli.Infrastructure;
using Cli.Services.Interfaces;

namespace Cli.Services;

public class PipelineService : IPipelineService
{
	private readonly RailwayDbContext _dbContext;

    public PipelineService()
    {
        _dbContext = LifetimeService.Get<RailwayDbContext>();
    }

    public async Task Add(Pipeline pipeline) =>
		await _dbContext.Pipelines.AddAsync(pipeline);
	
	public async Task Save() =>
		await _dbContext.SaveChangesAsync();
}
