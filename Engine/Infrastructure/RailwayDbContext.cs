using Railway.Engine.Domain;

using Microsoft.EntityFrameworkCore;

namespace Railway.Engine.Infrastructure;

public class RailwayDbContext : DbContext
{
	public DbSet<Pipeline> Pipelines => Set<Pipeline>();
	public DbSet<Repository> Repositories => Set<Repository>();
	public DbSet<RepositoryPipeline> RepositoryPipelines => Set<RepositoryPipeline>();
	public DbSet<Run> Runs => Set<Run>();
	public DbSet<RunJob> RunJobs => Set<RunJob>();
	public DbSet<RunStep> RunSteps => Set<RunStep>();
	
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

		modelBuilder.Entity<Run>(entity =>
		{
			entity.HasKey(x => x.Id);

			entity.Property(x => x.PipelineFileName)
				.HasMaxLength(100)
				.IsRequired();

			entity.Property(x => x.PipelineName)
				.IsRequired();

			entity.HasOne<Repository>()
				.WithMany()
				.HasForeignKey(x => x.RepositoryId)
				.OnDelete(DeleteBehavior.Cascade);

			entity.HasMany(x => x.Jobs)
				.WithOne(x => x.Run)
				.HasForeignKey(x => x.RunId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<RunJob>(entity =>
		{
			entity.HasKey(x => x.Id);

			entity.Property(x => x.Name)
				.IsRequired();

			entity.HasMany(x => x.Steps)
				.WithOne(x => x.Job)
				.HasForeignKey(x => x.RunJobId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		modelBuilder.Entity<RunStep>(entity =>
		{
			entity.HasKey(x => x.Id);

			entity.Property(x => x.Name)
				.IsRequired();

			entity.Property(x => x.Command)
				.IsRequired();
		});
    }
}
