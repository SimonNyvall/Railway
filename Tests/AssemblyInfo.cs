// Many test fixtures mutate process-global state (current working directory via
// TempGitRepository/TestRailwayRepository, static AnsiConsole). xunit would otherwise run
// these collections in parallel, causing cwd races and nondeterministic failures.
// xunit v3 4.x: [assembly: Parallelization(Mode = ParallelMode.None)] (Xunit.v3 / Xunit.Sdk).
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
