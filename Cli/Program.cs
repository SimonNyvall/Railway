using Cli.Commands;
using Cli.Handlers;
using Cli.Handlers.Interfaces;
using Cli.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Railway.Engine;
using Railway.Engine.Infrastructure;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddEngine();

// Detached-process tracking: one shared instance, registered before any provider is built so the
// Ctrl+C handler below kills exactly the children the command graph spawned. A type-based singleton
// would not work here — Spectre builds a fresh provider per run (see MsDiTypeRegistrar.Build).
var spawnedProcesses = new SpawnedProcessTracker();
services.AddSingleton<ISpawnedProcessTracker>(spawnedProcesses);

// CLI handlers
services.AddTransient<IInitHandler, InitHandler>();
services.AddTransient<IValidationHandler, ValidationHandler>();
services.AddTransient<IListHandler, ListHandler>();
services.AddTransient<IRunHandler, RunHandler>();
services.AddTransient<IDashboardHandler, DashboardHandler>();
services.AddTransient<IConvertHandler, ConvertHandler>();
services.AddTransient<IHooksInstallHandler, HooksInstallHandler>();
services.AddTransient<IHooksUninstallHandler, HooksUninstallHandler>();
services.AddTransient<IHooksStatusHandler, HooksStatusHandler>();
services.AddTransient<IHooksSyncHandler, HooksSyncHandler>();
services.AddTransient<IHooksRunHandler, HooksRunHandler>();
services.AddTransient<IHooksWatchHandler, HooksWatchHandler>();

// command types (Spectre also re-registers them via the registrar)
services.AddTransient<InitCommand>();
services.AddTransient<ValidateCommand>();
services.AddTransient<ListCommand>();
services.AddTransient<RunCommand>();
services.AddTransient<DashboardCommand>();
services.AddTransient<ConvertCommand>();
services.AddTransient<HooksInstallCommand>();
services.AddTransient<HooksUninstallCommand>();
services.AddTransient<HooksStatusCommand>();
services.AddTransient<HooksSyncCommand>();
services.AddTransient<HooksRunCommand>();
services.AddTransient<HooksWatchCommand>();

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

    config
        .AddCommand<ConvertCommand>("convert")
        .WithDescription(
            "Convert a GitHub Actions / GitLab / Azure DevOps YAML file into a Railway pipeline."
        );

    config.AddBranch<HooksSettings>(
        "hooks",
        hooks =>
        {
            hooks.SetDescription("Manage the git hooks materialized from pipeline triggers.");

            hooks
                .AddCommand<HooksInstallCommand>("install")
                .WithDescription("Install git hooks for the repository's pipeline triggers.");

            hooks
                .AddCommand<HooksUninstallCommand>("uninstall")
                .WithDescription("Remove the git hooks railway installed (--purge also deletes logs).");

            hooks
                .AddCommand<HooksStatusCommand>("status")
                .WithDescription("Show trigger and git hook state per pipeline.");

            hooks
                .AddCommand<HooksSyncCommand>("sync")
                .WithDescription("Re-materialize git hooks from the pipeline YAML and report the diff.");

            hooks
                .AddCommand<HooksRunCommand>("run")
                .WithDescription("Fire a hook event for the subscribed pipelines (headless).");

            hooks
                .AddCommand<HooksWatchCommand>("watch")
                .WithDescription("Watch the repository and run the pipelines with watch triggers until Ctrl+C.");
        });

#if DEBUG
    config.PropagateExceptions();
    config.ValidateExamples();
#endif
});

// Ctrl+C handling: the first signal cancels the supplied token so commands (notably `hooks watch`)
// shut down gracefully and release any held hook lock; a second signal falls through to the default
// handler so a hung run can always be killed.
using var cancellation = new CancellationTokenSource();

// Cancellation also terminates detached children this process spawned (`hooks run --detach`).
// They are meant to outlive the CLI, but not a cancelled one: without this they would keep running
// after the hook lock they were guarded by has been released.
using var killDetachedOnCancel = cancellation.Token.Register(() => spawnedProcesses.KillTracked());
var cancelSignals = 0;

void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
{
    if (Interlocked.Exchange(ref cancelSignals, 1) == 1)
    {
        // Second Ctrl+C: leave eventArgs.Cancel = false so the runtime terminates the process.
        return;
    }

    eventArgs.Cancel = true;
    cancellation.Cancel();
}

Console.CancelKeyPress += OnCancelKeyPress;

try
{
    return await app.RunAsync(args, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= OnCancelKeyPress;
}
