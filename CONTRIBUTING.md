# Contributing to Railway

Thanks for your interest in contributing! Railway is a local-first CI pipeline runner built with .NET 10, Spectre.Console.Cli, EF Core SQLite, and YamlDotNet.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (required for building and testing)
- Git (required for trigger/hook functionality and testing)
- A Unix-like shell (macOS/Linux) or cmd/PowerShell (Windows) — behavior is platform-aware

## Getting Started

```bash
# Clone the repo
git clone https://github.com/SimonNyvall/Railway.git
cd Railway

# Restore and build
dotnet build Railway.slnx

# Run all tests
dotnet test

# Try the CLI
dotnet run --project Cli -- --help
dotnet run --project Cli -- dashboard

# Try the MCP server
dotnet run --project Mcp
```

## Project Structure

| Project | Purpose |
|---|---|
| `Cli/` | Spectre.Console.Cli adapter — commands, handlers (renderers), exception→exit-code mapping. No Engine logic. |
| `Engine/` | Core domain/services/orchestration — pipeline parsing, validation, execution, hooks, schedulers, DB. No Spectre, no cwd dependence, no static locator. |
| `Mcp/` | Local-only MCP adapter over Engine (stdio transport). Exposes tools with path resolution and progress streaming. |
| `Tests/` | Unit (`Tests/Unit/`), Integration (`Tests/Integration/`), Infrastructure (`Tests/Infrastructure/`). Uses xUnit v3 + FakeItEasy. |

Key directories in Engine: `Domain/` (entities/exceptions), `Helpers/` (parsing/paths/hooks), `Infrastructure/` (DbContext), `Services/` (discovery/validation/runner/history/convert/notifications/schedulers), `Orchestration/` (coordinators like RepositoryInitializer, PipelineRunner, HookRunner, HookInstaller).

## Building & Testing

```bash
# Build
dotnet build Railway.slnx

# Run all tests
dotnet test

# Run with more detail
dotnet test --logger "console;verbosity=minimal"
```

**Test conventions:**
- Unit tests mock interfaces (FakeItEasy/Moq patterns appear; Tests use FakeItEasy primarily).
- Integration tests use temp git repos + temp SQLite databases.
- Integration tests are serialized via `[Collection("Integration Tests")]` (they mutate process cwd and static state).
- Tests may mutate `AnsiConsole.Console` and process cwd — be careful running them in parallel with other processes.
- `PipelineHelper.TryParse` catches broad `Exception` deliberately (don't "narrow" without understanding UX impact).

## Coding Conventions

From `AGENTS.md` (authoritative):
- Be extremely concise in interactions/responses.
- Never search DLLs/precompiled binaries/node_modules if source exists locally.
- Never clone a repo that already exists.
- For cross-repo tasks, inspect relevant local repos first.
- For C# changes, run tests for the affected project before reporting completion.
- For frontend changes, do not run builds/tests/dev servers as verification.
- Distinguish verified facts from assumptions.
- Ask only questions whose answers could materially affect the solution.

Project-specific rules:
- Engine must have **no Spectre, no cwd dependence, no static locator**.
- Handlers in CLI are renderers — they call Engine orchestration and render results; domain logic lives in Engine.
- Commands map Engine exceptions to messages/exit codes (see `Cli/Commands/ExceptionMappers`).
- Progress flows via `IProgress<RunEvent>` (Engine emits events; hosts render/ignore).
- All repository paths are explicit (`startPath`/`path`) — never read `Directory.GetCurrentDirectory()` in Engine.
- stdout is reserved for protocol in MCP server; all MCP logs go to stderr.
- `RunStatus.Succeeded = 0` — uninitialized enum means Succeeded; always set explicitly.
- EF: `Pipeline.Jobs`/`Variables`/`Triggers`/`Notify` are ignored by EF (not mapped). `RunHistory` schema has idempotent ALTER TABLE shims on startup.

## Adding Features

### Adding a CLI Command

1. Define settings class in `Cli/Commands/` (with validation attributes as needed).
2. Create handler interface in `Cli/Handlers/Interfaces/` and implementation in `Cli/Handlers/` (renderer; delegates to Engine).
3. Register in `Cli/Program.cs`: add to DI, register command type, add to `app.Configure` branch.
4. Map Engine exceptions to exit codes/messages in command or shared mapper.
5. Add unit/integration tests in `Tests/`.

### Extending Pipeline Schema

1. Update domain models in `Engine/Domain/` (e.g., `Pipeline`, `Job`, `Step`, `TriggerDefinition`).
2. Update YAML parsing in `Engine/Helpers/PipelineHelper.cs` (custom converters if needed; preserve `UnderscoredNamingConvention`, `IgnoreUnmatchedProperties`).
3. Update validation in `Engine/Services/ValidateService.cs`/orchestration validators.
4. Update serialization/write path (private write models) to avoid leaking EF members.
5. Add parsing/validation/execution tests.

### Adding MCP Tools

1. Add tool method to a tool class in `Mcp/Tools/` (or create new). Tools use `RailwayPathResolver` for paths.
2. Map DTOs via `Mcp/Tools/ToolDtoMapper.cs`.
3. Handle progress via `RunEventProgressAdapter` if running pipelines.
4. Register tool class in `Mcp/Program.cs` (`WithTools<...>()`).
5. Update `Mcp/README.md` and this README's MCP section.

## Development Workflow

1. **Explore first**: Understand the codebase before making changes. Prefer reading existing code/tests over guessing.
2. **Keep changes focused**: Match existing style and architecture boundaries (Engine vs Cli vs Mcp).
3. **Test early**: Run unit tests for affected projects. For Engine changes, run `dotnet test` for `Tests/Unit/Engine*`-related or all; integration tests are slower and serialized.
4. **Verify behavior**: Manually test CLI commands against a temp repo (`dotnet run --project Cli -- ...`).
5. **Document**: Update README.md, Mcp/README.md, or XML docs if behavior/CLI surface changes.
6. **Commit**: Use clear, descriptive commit messages. Keep commits focused.

## Debugging Tips

- **Engine exceptions**: CLI maps them to user-facing messages; run with DEBUG builds in CLI to see full stack traces (see `Program.cs` `#if DEBUG`).
- **MCP protocol**: stdout is MCP protocol only — logs go to stderr. Use stderr logging or an external log file when debugging MCP.
- **DB location**: Check app data dir (`DatabasePathHelper`) if inspecting run history.
- **Hooks**: Use `railway hooks status` and inspect `.railway/hooks/`, git hooks (`git rev-parse --git-path hooks`), and scheduler entries. Use `RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT` for testing schedulers.
- **Cancellation**: First Ctrl+C cancels gracefully (releases hook locks); second forces termination.

## Submitting Changes

1. Ensure `dotnet build Railway.slnx` succeeds with no warnings introduced (if possible).
2. Ensure `dotnet test` passes (all tests).
3. Update documentation if public surface changes (commands, flags, schema, MCP tools).
4. Open a pull request against `main` with a clear description of changes, rationale, and testing done.

## License

By contributing, you agree that your contributions will be licensed under the MIT License. See [LICENSE](LICENSE).
