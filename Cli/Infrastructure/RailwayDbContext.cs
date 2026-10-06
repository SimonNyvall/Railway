using Cli.Domain;

using Microsoft.EntityFrameworkCore;

namespace Cli.Infrastructure;

public class RailwayDbContext : DbContext
{
	public DbSet<Pipeline> Pipelines => Set<Pipeline>();
	public DbSet<Repository> Repositories => Set<Repository>();
	public DbSet<RepositoryPipeline> RepositoryPipelines => Set<RepositoryPipeline>();
	
	public RailwayDbContext(DbContextOptions<RailwayDbContext> options)
		: base(options)
		{
			
		}

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
		
		modelBuilder.Entity<Pipeline>(entity =>
		{
			entity.HasKey(x => x.Id);
			
			entity.Property(x => x.Path)
				.HasMaxLength(100)
				.IsRequired();
				
			entity.Ignore(e => e.Jobs);
			entity.Ignore(e => e.Variables);
		});
		
		modelBuilder.Entity<Repository>(entity =>
		{
			entity.HasKey(x => x.Id);
			
			entity.Property(x => x.Path)
				.HasMaxLength(100)
				.IsRequired();
		});
		
		modelBuilder.Entity<RepositoryPipeline>(entity =>
		{
			entity.HasKey(x => new
			{
				x.RepositoryId,
				x.PipelineId
			});
			
			entity.HasOne(x => x.Repository)
				.WithMany(x => x.RepositoryPipelines)
				.HasForeignKey(x => x.RepositoryId)
				.OnDelete(DeleteBehavior.Cascade);
				
			entity.HasOne(x => x.Pipeline)
				.WithMany(x => x.RepositoryPipelines)
				.HasForeignKey(x => x.PipelineId)
				.OnDelete(DeleteBehavior.Cascade);
		});
    }
}
