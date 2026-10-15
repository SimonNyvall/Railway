using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Helpers;
using Cli.Infrastructure;
using Cli.Services;
using Cli.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Spectre.Console.Cli;

await RegisterServices();

var app = new CommandApp<DashboardCommand>();

app.Configure(config =>
{
    config.SetApplicationName("railway");
    config.SetApplicationVersion("0.0.1");

    config.AddCommand<InitCommand>("init").WithDescription("Initialize a railway repository");

    config
        .AddCommand<ValidateCommand>("validate")
        .WithDescription("Validate a yaml pipeline file.");

    config
        .AddCommand<ListCommand>("list")
        .WithDescription("List the pipelines in the current repository.");

    config
        .AddCommand<RunCommand>("run")
        .WithDescription("Run a pipeline in the current repository.");

#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
});

return await app.RunAsync(args);

async Task RegisterServices()
{
    LifetimeService.Add<IInitHandler, InitHandler>();

    LifetimeService.Add<IPipelineService, PipelineService>();

    LifetimeService.Add<IValidationHandler, ValidationHandler>();
    LifetimeService.Add<IValidateService, ValidateService>();

    LifetimeService.Add<IDashboardHandler, DashboardHandler>();
    LifetimeService.Add<IDashboardService, DashboardService>();

    LifetimeService.Add<IListHandler, ListHandler>();
    LifetimeService.Add<IPipelineDiscoveryService, PipelineDiscoveryService>();

    LifetimeService.Add<IRunHandler, RunHandler>();
    LifetimeService.Add<IRunnerService, RunnerService>();

    var databasePath = DatabasePathHelper.Get();

    if (!DatabasePathHelper.CreatePathIfNotExist(databasePath))
    {
        Console.WriteLine($"The path: {databasePath} could not be created");
    }

    var options = new DbContextOptionsBuilder<RailwayDbContext>()
        .UseSqlite($"Data Source={databasePath}/data.db")
        .Options;

    // Create and ensure database exists
    await using (var dbContext = new RailwayDbContext(options))
    {
        await dbContext.Database.EnsureCreatedAsync();
    }

    // Register factory for creating new contexts per request
    LifetimeService.Add<RailwayDbContext>(() => new RailwayDbContext(options));
}
