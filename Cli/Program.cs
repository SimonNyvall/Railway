using Cli.Helpers;
using Cli.Infrastructure;
using Spectre.Console.Cli;

using Microsoft.EntityFrameworkCore;
using Cli.Commands;
using Cli.Services;
using Cli.Handlers.Interfaces;
using Cli.Handlers;
using Cli.Services.Interfaces;

await RegisterServices();

var app = new CommandApp();

app.Configure(config =>
{
	config.SetApplicationName("railway");
	config.SetApplicationVersion("0.0.1");
	
	config.AddCommand<LinkCommand>("link")
		.WithDescription("link a piple to a repo")
		.WithAlias("l");
		
#if DEBUG
	config.PropagateExceptions();
	config.ValidateExamples();
#endif
});

return await app.RunAsync(args);

async Task RegisterServices()
{
	LifetimeService.Add<ILinkHandler, LinkHandler>();
	
	var databasePath = DatabasePathHelper.Get();

	if (!DatabasePathHelper.CreatePathIfNotExist(databasePath))
	{
		Console.WriteLine($"The path: {databasePath} could not be created");
	}

	var options = new DbContextOptionsBuilder<RailwayDbContext>()
		.UseSqlite($"Data Source={databasePath}/data.db")
		.Options;
	
	await using var dbContext = new RailwayDbContext(options);
	await dbContext.Database.EnsureCreatedAsync();
	
	LifetimeService.Add(dbContext);
	
	LifetimeService.Add<IPipelineService, PipelineService>();
}


