using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Railway.Engine;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddEngine();

// CLI handlers
services.AddTransient<IInitHandler, InitHandler>();
services.AddTransient<IValidationHandler, ValidationHandler>();
services.AddTransient<IListHandler, ListHandler>();
services.AddTransient<IRunHandler, RunHandler>();
services.AddTransient<IDashboardHandler, DashboardHandler>();

// command types (Spectre also re-registers them via the registrar)
services.AddTransient<InitCommand>();
services.AddTransient<ValidateCommand>();
services.AddTransient<ListCommand>();
services.AddTransient<RunCommand>();
services.AddTransient<DashboardCommand>();

// Bootstrap DB (Engine owns EnsureCreated). Preserve the old failure message text.
try
{
    using var bootstrap = services.BuildServiceProvider();
    await bootstrap.InitializeEngineAsync();
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
    return 1;
}

var registrar = new MsDiTypeRegistrar(services);

var app = new CommandApp<DashboardCommand>(registrar);

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
