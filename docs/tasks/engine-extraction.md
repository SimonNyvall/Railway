# Handoff: Extract core pipeline logic into `Engine`

Status: **implemented (steps 0–9 complete)**.

## 1. Goal

Extract all non-presentation pipeline logic out of the `Cli` project into a new
`Engine` project so that future hosts (an MCP server, cron jobs, git-hooks) can
reuse it. Today everything lives in `Cli/`. After this work:

- `Engine` owns the domain model, persistence, YAML parsing, repository
  discovery, validation, execution and **use-case orchestration**.
- `Cli` becomes a thin Spectre.Console adapter: argument parsing + rendering of
  Engine results.
- `Engine` has **no** reference to Spectre.Console, no static service locator,
  and no dependence on the process working directory.

The MCP project is **not built** in this change; Engine is only made
MCP-compatible.

## 2. Agreed decisions (from clarification)

| # | Decision | Choice |
|---|----------|--------|
| D1 | Engine boundary | **Core + orchestration**: Engine owns run/validate/list/dashboard/init flows as non-rendering methods returning structured results |
| D2 | Progress reporting | **`IProgress<RunEvent>`** — Engine emits structured events; hosts render or ignore |
| D3 | Event model | **Rich typed `RunEvent`**: kind enum + fields (see §5) |
| D4 | DI | **MS.DI extension in Engine** (`AddEngine`); Engine services are resolved via `IServiceCollection`/`IServiceProvider`, not `LifetimeService` |
| D5 | Repository resolution | **Explicit path parameter** — Engine methods take a start/root path; CLI passes cwd |
| D6 | Project name / layout | Folder `Engine/`, assembly + root namespace **`Railway.Engine`** |
| D7 | Tests | **Single `Tests` project** referencing both `Engine` and `Cli` |
| D8 | DB + bootstrap | **Engine owns** `RailwayDbContext`, DB path, and `EnsureCreated` bootstrap; fix the `DatabasePathHelper` macOS/Linux bug |
| D9 | MCP | **Enable only** — do not create an MCP project |
| D10 | CLI behavior | **Strictly preserved** — same commands, output/markup, messages, exit codes |
| D11 | Landing | One handoff (this doc), split into **small, individually buildable steps** |

## 3. Target layout

```
Engine/
  Engine.csproj                      (classlib, net10.0, ns Railway.Engine, NO Spectre)
  Properties/AssemblyInfo.cs         [InternalsVisibleTo("Tests")]
  Domain/                            Railway.Engine.Domain
  Helpers/                           Railway.Engine.Helpers
  Infrastructure/                    Railway.Engine.Infrastructure
  Services/                          Railway.Engine.Services(.Interfaces)
  Orchestration/                     Railway.Engine.Orchestration(.Interfaces)  (step 8)
  EngineOptions.cs
  ServiceCollectionExtensions.cs     AddEngine(...)
Cli/
  Commands/                          unchanged responsibilities
  Rendering/                         former handler render logic (steps 5, 8)
  Program.cs                         builds ServiceProvider via AddEngine
Tests/                               references Engine + Cli
```

Engine package references: `Microsoft.EntityFrameworkCore.Sqlite`,
`Microsoft.Extensions.DependencyInjection` (for `AddEngine`), `YamlDotNet`.
**No** `Spectre.Console*`.

## 4. Current-state findings (verified)

Verified by reading every file below in full.

**Already Engine-clean (no Spectre, no CLI concerns)**
- `Cli/Domain/*` (15 types; only `System.ComponentModel.DataAnnotations` attrs)
- `Cli/Helpers/{PipelineHelper, RailwayManifestHelper, RepositoryHelper, DatabasePathHelper}`
- `Cli/Infrastructure/RailwayDbContext`
- `Cli/Services/{ValidateService, PipelineDiscoveryService, PipelineService, DashboardService}` + interfaces

**Blockers / coupling**
- `Cli/Services/RunnerService.cs` — execution engine, but `ExecuteStep` (lines
  ~204–287) hardcodes static `AnsiConsole`: step banner (218/223), verbose `$ cmd`
  (221), stdout stream (257), stderr red (271). No `IAnsiConsole` injection.
- All 5 `Cli/Handlers/*` — orchestration **mixed with** rendering
  (`AnsiConsole.MarkupLine`). `ValidationHandler` has `PrintValidOutput`/
  `PrintInvalidOutput`.
- `Cli/Services/LifetimeService.cs` — static locator; consumed by all Commands,
  all Handlers, and `RunnerService`/`PipelineService`/`DashboardService` in
  parameterless ctors.
- `Cli/Helpers/RepositoryHelper.cs` — `FindUpwards` uses
  `Directory.GetCurrentDirectory()` (cwd-global).
- `Cli/Helpers/DatabasePathHelper.cs` — **bug**: Linux branch uses
  `~/Library/something`; macOS branch uses `~/.local/share`/`XDG_DATA_HOME`
  (swapped).
- `Cli/Domain/NotARailwayRepositoryException.cs` — message embeds CLI hint text.
- Registration map **triplicated**: `Cli/Program.cs`,
  `Tests/Infrastructure/TestServiceRegistrar.cs`,
  `Tests/Infrastructure/TestTypeRegistrar.cs` (smell R1).

**Dead/legacy**
- `Cli/Domain/RunEvent.cs` — stub `{ string Text }`, unused → replaced in step 5.
- `PipelineHelper.GetPipelineNames()` / `GetPipelinePath()` — legacy env-var code,
  unused.
- `Cli/Services/PipelineService` — no consumer found.
- `Cli/Cli.csproj` references `Microsoft.Extensions.Hosting` and
  `Microsoft.Extensions.Configuration.Binder` — no source usage found (assumption,
  grep-confirm before removing).

**Facts that constrain "preserve UX"**
- Exact CLI strings that must not change: see §7.
- `RunStatus.Succeeded = 0`; runner sets status explicitly.
- `Pipeline.Jobs`/`Variables` are `[NotMapped]` (EF `Ignore`).
- Tests mutate the process cwd (`Directory.SetCurrentDirectory`) and the static
  `AnsiConsole.Console`; integration tests are serialized via
  `[Collection("Integration Tests")]`.

## 5. Target API shape (Engine)

**Run event model** (replaces stub `RunEvent`):

```csharp
public enum RunEventKind
{
    RunStarted, JobStarted, StepStarted,
    StandardOutput, StandardError,
    StepCompleted, JobCompleted, RunCompleted
}

public sealed class RunEvent
{
    public RunEventKind Kind { get; init; }
    public int RunId { get; init; }
    public string? JobName { get; init; }
    public string? StepName { get; init; }
    public string? Text { get; init; }      // output lines; step command when verbose
    public int? ExitCode { get; init; }
    public RunStatus? Status { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```

**Runner**

```csharp
Task<Run> Run(RunRequest request, IProgress<RunEvent>? progress = null,
              CancellationToken cancellationToken = default);
```

`RunRequest` keeps `RepositoryPath`, `PipelineFileName`, `Pipeline`, `Verbose`
(add a working/repo-root path when needed for step 7).

**DI**

```csharp
public sealed class EngineOptions
{
    public string? DatabasePath { get; init; }        // default: DatabasePathHelper.Get()
    public bool InitializeDatabase { get; init; } = true;
}

public static IServiceCollection AddEngine(this IServiceCollection services,
                                           EngineOptions? options = null);
```

Because `AddEngine` cannot be async, database creation is triggered by an
explicit initializer (e.g. `IEngineInitializer.InitializeAsync(IServiceProvider)`
or `await provider.InitializeEngineAsync()`), called by the host at startup.
CLI and tests both call it. Return shape to be finalized in step 6.

**Orchestration (step 8)** — one interface per use case, returning records:
- `IRepositoryInitializer` → `InitResult`
- `IPipelineValidator` → `ValidationResult { Pipeline, IReadOnlyList<string> Errors }`
- `IPipelineLister` → `IReadOnlyList<DiscoveredPipeline>`
- `IDashboardQuery` → `DashboardView`
- `IPipelineRunner` → `RunOutcome { Run }` (+ `IProgress<RunEvent>`)

Engine throws domain exceptions for failures
(`NotARailwayRepositoryException`, plus new `PipelineFileNotFoundException`,
`AmbiguousPipelineException`, `PipelineInvalidException`) instead of rendering or
`AnsiConsole`; CLI commands map them to exit codes and messages.

## 6. Steps (each should build + keep tests green on its own)

> Order matters. Steps 0–4 are pure moves (no behavior change). Behavior changes
> start at step 5.

### Step 0 — Create the Engine project and wire it up (no moves)
- Add `Engine/Engine.csproj` (classlib, `net10.0`, ImplicitUsings, Nullable,
  `RootNamespace`/`AssemblyName` = `Railway.Engine`, package refs per §3).
- Add `Engine/Properties/AssemblyInfo.cs` with `[InternalsVisibleTo("Tests")]`.
- Add Engine to `Railway.slnx`; add `ProjectReference` to `Engine` in
  `Cli.csproj` and `Tests.csproj`.
- **Verify:** `dotnet build Railway.slnx` and `dotnet test` green; no source moved.

### Step 1 — Move `Domain` → `Engine/Domain`
- Move all `Cli/Domain/*.cs` → `Engine/Domain/`; namespace `Cli.Domain` →
  `Railway.Engine.Domain`. Keep `RunEvent` stub as-is (rewritten in step 5).
- Keep `NotARailwayRepositoryException` message unchanged for now.
- Update `using` references in `Cli` and `Tests`.
- **Verify:** build + tests green, output unchanged.

### Step 2 — Move `Helpers` → `Engine/Helpers`
- Move `PipelineHelper`, `RailwayManifestHelper`, `RepositoryHelper`,
  `DatabasePathHelper`; namespace → `Railway.Engine.Helpers`.
- Do **not** change behavior yet (RepositoryHelper still reads cwd;
  DatabasePathHelper bug still present).
- Update usings.
- **Verify:** build + tests green.

### Step 3 — Move `Infrastructure` → `Engine/Infrastructure`
- Move `RailwayDbContext`; namespace → `Railway.Engine.Infrastructure`.
- Update usings.
- **Verify:** build + tests green.

### Step 4 — Move Spectre-free services → `Engine/Services`
- Move `ValidateService`, `PipelineDiscoveryService`, `PipelineService`,
  `DashboardService` and their interfaces; namespace →
  `Railway.Engine.Services(.Interfaces)`.
- Keep `DashboardService`'s direct `PipelineDiscoveryService.DiscoverStatic` call
  (decouple later if desired).
- `RunnerService`/`IRunnerService` stay in `Cli` for now (it still uses Spectre).
- **Verify:** build + tests green.

### Step 5 — Rich `RunEvent` + decouple runner from Spectre; move runner to Engine
- Replace stub `RunEvent` with the model in §5 (in `Engine/Domain`).
- Change `IRunnerService.Run` to accept `IProgress<RunEvent>?` (see §5).
- In `RunnerService.ExecuteStep` and `Run`, **remove all `AnsiConsole` usage** and
  emit events instead:
  - `StepStarted` (carries step name; `Text` = command so the host can show
    `$ cmd` only when verbose),
  - `StandardOutput` / `StandardError` per line,
  - `StepCompleted` (ExitCode), and `RunStarted`/`JobStarted`/`JobCompleted`/
    `RunCompleted` as appropriate.
- Add a CLI renderer (e.g. `Cli/Rendering/SpectreRunProgress : IProgress<RunEvent>`)
  that reproduces **byte-identical** markup via the same
  `AnsiConsole.MarkupLine` calls: step banner, verbose `$ cmd`, escaped stdout,
  red escaped stderr.
- Move `RunnerService` + `IRunnerService` → `Engine/Services`.
  Keep `TopologicalSort`/`BuildEnvironment`/`MergeEnvironments` `internal static`
  (Tests uses them via `InternalsVisibleTo`).
- `RunHandler` passes the renderer as `progress`.
- **Verify:** build + tests green; assert CLI output strings in
  `RunCommandTests` unchanged (banner, `Hello World`, success/fail lines).

### Step 6a — `AddEngine` + `EngineOptions` (Engine only)
- Add `EngineOptions` and `ServiceCollectionExtensions.AddEngine` (§5).
- Register `RailwayDbContext` (factory over `DbContextOptions`) and all Engine
  services/interfaces.
- Do **not** rewire `Cli` yet.
- **Verify:** build green; add a focused Engine unit test that resolves services
  from an `IServiceCollection`.

### Step 6b — Fix `DatabasePathHelper` + Engine bootstrap
- Fix OS branches: Windows → `LocalApplicationData`; Linux → `XDG_DATA_HOME` ??
  `~/.local/share`; macOS → `~/Library/Application Support`. Keep
  `applicationFolder = "Railway"`.
- Add the DB initializer (create dir + `EnsureCreated`) invoked from the host.
- **Verify:** build green; add unit tests covering per-OS path selection (inject
  OS or use `OperatingSystem.IsX` guards; document approach).

### Step 6c — Rewire `Cli` to MS.DI; delete `LifetimeService`
- `Cli/Program.cs`: build an `IServiceProvider` via `AddEngine(...)` +
  `InitializeEngineAsync`, register CLI handlers/renderers/commands, then run
  Spectre `CommandApp` with an `ITypeRegistrar` backed by MS.DI (mirror
  `Tests/Infrastructure/TestTypeRegistrar.cs`). Commands take their handler via
  constructor injection.
- Delete `Cli/Services/LifetimeService.cs`.
- Update `Tests/Infrastructure/*`: `TestTypeRegistrar` uses `AddEngine`;
  `TestServiceRegistrar` removed/repurposed; fix `InitCommandTests`'
  direct `LifetimeService.Get<IInitHandler>()` usage.
- Resolves smell R1 (registration no longer triplicated).
- **Verify:** build + tests green; CLI output unchanged.

### Step 7 — Remove cwd-global repository discovery
- Change `RepositoryHelper` to take an explicit start path (e.g.
  `FindUpwards(string startPath, ...)`, `GetRailwayPath(string startPath)`).
- Engine callers (`DashboardService`, orchestration) receive the path explicitly.
- CLI passes `Directory.GetCurrentDirectory()` (UX unchanged).
- **Verify:** build + tests green.

### Step 8 — Extract orchestration into Engine; handlers become renderers
- Add per-use-case Engine interfaces + result records (§5). Move the
  resolve→parse→validate→run (and validate/list/dashboard/init) flows out of
  `Cli/Handlers/*` into `Engine/Orchestration`.
- Engine throws domain exceptions (no rendering, no `AnsiConsole`).
- `Cli/Handlers/*` (or `Cli/Rendering/*`) call Engine and render the result with
  the **exact** markup listed in §7; `Commands` map exceptions → exit codes.
- **Verify:** build + tests green; every output/exit-code assertion unchanged.

### Step 9 — Cleanup + docs
- Delete dead code: `PipelineHelper.GetPipelineNames`/`GetPipelinePath`
  (env legacy); `PipelineService` if still unused (confirm first); remove unused
  `Cli.csproj` package refs (`Hosting`, `Configuration.Binder`, and any now-unused
  `YamlDotNet`/EF refs — grep-confirm).
- Optionally narrow `PipelineHelper.TryParse`'s `catch (Exception)`.
- Update `AGENTS.md` (architecture, project list) and the MVP doc status.
- Document future MCP integration points (AddEngine; `IProgress<RunEvent>`;
  explicit repo path).
- **Verify:** full `dotnet build Railway.slnx` + `dotnet test` green.

## 7. Acceptance criteria / behavior-preservation checklist

- Commands unchanged: `init`, `validate <NAME>`, `list`, `run <FILE_NAME>
  [--verbose]`, default `dashboard`.
- Exit codes unchanged: `0` success, `1` failure.
- Exact strings preserved (verified from current handlers/runner):
  - Run not found: `Pipeline file not found: {fileName}` and
    `Hint: run 'railway list' to see available pipelines`.
  - Ambiguous: `Multiple pipeline files match: {fileName}` then `  {p.Path}`.
  - Parse error: `Failed to parse pipeline: {fileName}` then `  ✗ {parseError}`.
  - Invalid: `Pipeline is invalid: {fileName}` then `  ✗ {error}` per error.
  - Success: `✓ Pipeline completed successfully`.
  - Failure: `✗ Pipeline failed with exit code {run.ExitCode}`.
  - Validate valid/invalid blocks (`Pipeline:`, `✓ Pipeline is valid`,
    `Jobs:`, per-job/step lines, `✗ Pipeline is invalid`, `Errors:`).
  - List: `No pipelines found` when empty; `{✗?}{FileName} - name: {name}`.
  - Dashboard: `Repository:`, `Pipelines:`, `No pipelines configured`,
    `Recent Runs:`, `No runs yet`, per-run status markup.
  - Init messages incl. `fatal: not a git repository ...` and
    `Initialized empty Railway repository in ...` /
    `Reinitialized existing Railway repository in ...`.
  - Runner step banner `┌─ Step: {name} ... ┐` / `└...┘`, verbose `$ {cmd}`.
  - `NotARailwayRepositoryException` message
    (`fatal: not a railway repository (or any parent): .railway` + hint).
- `Engine/Engine.csproj` has **no** Spectre reference (enforce in review).
- `Engine` contains no `Directory.GetCurrentDirectory()` (after step 7) and no
  static service locator (after step 6c).
- MCP-enabling properties hold: reflection-free public API, MS.DI registration,
  `IProgress<RunEvent>` progress, explicit repo path.

## 8. Verification commands

```bash
dotnet build Railway.slnx
dotnet test
```

Per AGENTS.md: do not run the CLI/dev server; report completion and let the user
verify interactively.

## 9. Risks / out-of-scope notes

- **Static `AnsiConsole`** still used by the CLI renderer (unchanged). Test smell
  R2/R9 (inject `IAnsiConsole` into renderers) is **not** required here but
  recommended; doing it would let tests run in parallel.
- `RunStatus.Succeeded = 0` hazard and `RunnerService.Run` persisting a `Running`
  row before `TopologicalSort` can throw (R10) are **unchanged** by this work.
- `NotARailwayRepositoryException`'s hint text is CLI-flavoured; keep the message
  for UX preservation. Moving the hint into the CLI layer is optional and, if
  done, must keep the exact printed text.
- Integration tests mutate process cwd + static console, so they stay serialized.

## 10. Verified facts vs assumptions

**Verified (files read in full):** the current tree, all type names/namespaces,
Spectre coupling locations, `LifetimeService` consumers, triplicated registration,
`DatabasePathHelper` bug, `RunEvent` stub, `InternalsVisibleTo("Tests")`,
`Railway.slnx` contents, package versions, test structure and the exact CLI
strings in §7.

**Assumptions (confirm before acting):** that `Microsoft.Extensions.Hosting` and
`Microsoft.Extensions.Configuration.Binder` are unused in `Cli.csproj`; that
`PipelineService` has no consumer. Both should be grep-confirmed in step 9.

**Confirmed in step 9:** both assumptions held — those two package refs and
`PipelineService`/`IPipelineService` were removed.

## 11. Code-review findings (post-implementation)

Reviewed the unstaged changes against this handoff. Build + `dotnet test` are
green (72/72); all §7 strings and exit codes are covered by unchanged test
assertions (no `Assert.` line was added, removed, or modified).

Smells found (none blocking):

- **Duplicated Code** — `Orchestration/PipelineValidator.IsFileYaml` re-implements
  the extension check already exposed as `Helpers/PipelineHelper.IsYaml`. This
  duplication pre-dates the extraction (it lived in `ValidationHandler`) and was
  carried over. Suggested fix: call `PipelineHelper.IsYaml(path)` and delete the
  private helper (note the semantics differ slightly: `EndsWith` vs
  `Path.GetExtension`, both true for `*.yml`/`*.yaml`).
- **Middle Man** — `Orchestration/DashboardQuery` only forwards to
  `IDashboardService.GetView`. Deliberate per §5 (one interface per use case),
  so accepted; could be collapsed by having `DashboardService` implement
  `IDashboardQuery` directly if the indirection is later judged unnecessary.
- **Missing docs (low)** — the new public orchestration interfaces, result
  records, and domain exceptions have no XML doc comments, whereas
  `EngineInitializer`/`ServiceProviderExtensions`/`MsDiTypeRegistrar` do. Repo
  convention is mixed; consider documenting the new public surface.

Accepted-by-design (not smells): `NotARailwayRepositoryException` keeps its
CLI-flavoured message (per §9 UX preservation); `Cli/Handlers/*` rethrow the
legacy `FileNotFoundException`/`InvalidOperationException` types so
`Cli/Commands/*` catch-blocks and exit codes stay byte-identical.
