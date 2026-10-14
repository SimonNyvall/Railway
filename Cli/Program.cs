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
}
