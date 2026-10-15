# Railway CLI — Agent Guide

## Project Overview
- **.NET 10** CLI (`Spectre.Console.Cli`) for local CI pipelines defined in `.railway/pipelines/*.yaml`
- Single-project solution: `Cli/Cli.csproj` + `Tests/Tests.csproj`
- Persistence: EF Core + SQLite (`EnsureCreated()` on startup), DB in OS app-data dir
- Custom static service locator: `Cli/Services/LifetimeService.cs`

## Commands
```bash
dotnet build Railway.slnx          # Build
dotnet test                        # Run tests (xUnit + Moq)
```

## Key Architecture
- **Entry point**: `Cli/Program.cs` — registers commands & services via `LifetimeService`
- **Commands**: `init`, `validate`, `list`, `run`, default `dashboard`
- **Handlers** resolve deps via `LifetimeService.Get<T>()` in parameterless ctors
- **Pipeline YAML**: parsed by `PipelineHelper.cs` (YamlDotNet, UnderscoredNamingConvention)
- **Repo discovery**: walks up from cwd to find `.railway/` (git-style)

## Important Conventions
- All commands require `.railway` folder; fatal + hint to run `railway init` if missing
- `railway.yaml` = repo metadata + optional pipeline index (CLI-maintained)
- Pipeline files in `.railway/pipelines/` are source of truth
- Run history persisted to SQLite (`Run`, `RunJob`, `RunStep` entities)
- Exit codes: `0` success, `1` failure; `--verbose` for details

## Testing
- Unit tests: `Tests/Unit/` (mock interfaces via Moq)
- Integration tests: `Tests/Integration/` (temp git repo + temp SQLite)
- Test infrastructure: `Tests/Infrastructure/`

## Current MVP Status (from docs/tasks/mvp-local-pipelines.md)
- ✅ `validate` — working
- ✅ `init` — working
- 🔄 `list`, `run`, `dashboard` — in progress (WS3/WS4/WS5)

## Gotchas
- `LifetimeService` is a static locator; add interface-accepting ctor overloads for testability
- `Pipeline.Jobs` / `Variables` are `[NotMapped]` (ignored by EF)
- `RunStatus` enum has `Succeeded = 0` — uninitialized = Succeeded; always set explicitly
- `PipelineHelper.TryParse` catches broad `Exception` — narrow in future
- `DatabasePathHelper` has known macOS/Linux path swap issue (deferred)