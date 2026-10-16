# Railway CLI — Agent Guide

## Project Overview
- **.NET 10** CLI (`Spectre.Console.Cli`) for local CI pipelines defined in `.railway/pipelines/*.yaml`
- Solution `Railway.slnx` has four projects:
  - `Engine/` (`Railway.Engine`) — Domain / Helpers / Infrastructure / Services / Orchestration. **No Spectre, no cwd dependence, no static locator.**
  - `Cli/` — thin Spectre adapter: Commands map exceptions → exit codes; Handlers render Engine results.
  - `Mcp/` (`Railway.Mcp`) — thin MCP adapter over the Engine (stdio transport, local-only).
  - `Tests/` — xUnit, references `Engine`, `Cli`, and `Mcp`.
- Persistence: EF Core + SQLite (`EnsureCreated()` on startup), DB in OS app-data dir; owned by Engine.

## Commands
```bash
dotnet build Railway.slnx          # Build
dotnet test                        # Run tests (xUnit + Moq/FakeItEasy)
```

## Key Architecture
- **DI**: `services.AddEngine(EngineOptions?)` + `await provider.InitializeEngineAsync()`; `Cli/Program.cs` builds the `ServiceProvider`. `Cli/Infrastructure/MsDiTypeRegistrar` bridges MS.DI ↔ Spectre `ITypeRegistrar`. The old static `LifetimeService` is **deleted**.
- **Commands**: `init`, `validate`, `list`, `run`, default `dashboard` — registered in `Cli/Program.cs`.
- **Handlers are renderers**: they call Engine orchestration (`IRepositoryInitializer`, `IPipelineValidator`, `IPipelineLister`, `IDashboardQuery`, `IPipelineRunner`) and render results; `Cli/Rendering/SpectreRunProgress` renders run output.
- **Progress**: Engine emits `IProgress<RunEvent>` (`RunEventKind` enum: RunStarted/JobStarted/StepStarted/StandardOutput/StandardError/StepCompleted/JobCompleted/RunCompleted); hosts render or ignore.
- **Repository resolution**: explicit `startPath` parameter (CLI passes `Directory.GetCurrentDirectory()`); Engine never reads cwd.
- **Pipeline YAML**: parsed by `Engine/Helpers/PipelineHelper.cs` (YamlDotNet, UnderscoredNamingConvention).
- **MCP host**: `Mcp/Program.cs` builds the host → `AddEngine()` → `InitializeEngineAsync()` → `AddMcpServer().WithStdioServerTransport().WithTools<...>()`. Tools live in `Mcp/Tools/`, resolve paths via `RailwayPathResolver`, and map Engine exceptions via `EngineExceptionMapper`.

## Important Conventions
- All commands require `.railway` folder; fatal + hint to run `railway init` if missing
- `railway.yaml` = repo metadata + optional pipeline index (CLI-maintained)
- Pipeline files in `.railway/pipelines/` are source of truth
- Run history persisted to SQLite (`Run`, `RunJob`, `RunStep` entities)
- Exit codes: `0` success, `1` failure; `--verbose` for details
- Engine throws domain exceptions instead of rendering: `NotARailwayRepositoryException`, `NotAGitRepositoryException`, `PipelineFileNotFoundException`, `AmbiguousPipelineException`, `PipelineInvalidException` — CLI commands map them to messages/exit codes.

## MCP Server (`Mcp/` — `Railway.Mcp`)
- **Local-only, stdio transport**: the MCP client spawns the process; **never networked**, no HTTP listener, **no auth required**. No `--transport remote`.
- **stdout is reserved for the MCP protocol** — all logs go to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`); never `Console.WriteLine` to stdout.
- **Path resolution precedence** (per tool): explicit `path` argument → `RAILWAY_REPO_PATH` env var → process cwd. Engine then walks upward for `.railway`.
- **Tool inventory** (5): `list_pipelines(path?)`, `validate_pipeline(file, path?)`, `run_pipeline(file, verbose?, includeOutput?, path?)` (blocks, streams progress), `dashboard(path?)`, `runs(limit?, runId?, path?)`.
- Run locally: `dotnet run --project Mcp`; client wiring + example `.mcp.json` in `Mcp/README.md`.
- Engine integration points it consumes: `AddEngine(EngineOptions?)` + `await InitializeEngineAsync()`; `IProgress<RunEvent>`; explicit repository paths (never cwd); Engine domain exceptions handled via `EngineExceptionMapper` rather than parsing output. Public Engine API is reflection-free and MS.DI-resolvable.

## Testing
- Unit tests: `Tests/Unit/` (mock interfaces via Moq/FakeItEasy)
- Integration tests: `Tests/Integration/` (temp git repo + temp SQLite)
- Test infrastructure: `Tests/Infrastructure/`

## Current MVP Status (from docs/tasks/mvp-local-pipelines.md)
- ✅ `validate`, `init`, `list`, `run`, `dashboard` — all implemented
- ✅ Engine extraction (docs/tasks/engine-extraction.md steps 0–9) — complete

## Gotchas
- `Pipeline.Jobs` / `Variables` are `[NotMapped]` (ignored by EF)
- `RunStatus` enum has `Succeeded = 0` — uninitialized = Succeeded; always set explicitly
- `PipelineHelper.TryParse` catches broad `Exception` (deliberate deferral in step 9: narrowing to `YamlException` would change UX for IO/type-conversion failures) — narrow carefully with re-test
- `DatabasePathHelper` macOS/Linux path swap issue: **fixed** during extraction (step 6b); per-OS paths covered by tests
- Tests mutate process cwd + static `AnsiConsole.Console`; integration tests stay serialized via `[Collection("Integration Tests")]`
