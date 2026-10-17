# Railway

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

Local CI pipelines defined in YAML and triggered from git hooks, OS schedulers, file watchers, or manually. Runs with your shell and environment — no containers required. Ships with a local-only MCP server so AI assistants can list, validate, and run pipelines.

## Features

- **Local-first** — Executes commands in your shell using your existing environment and `PATH`. No Docker runtime required.
- **YAML pipelines** — Define jobs, steps, dependencies, environment variables, and notifications under `.railway/pipelines/*.yaml`.
- **Git-integrated triggers** — Materializes pipeline triggers into native git hooks and OS schedulers (`post-commit`, `push`, `post-merge`, `post-checkout`, `schedule`, `watch`).
- **Live execution** — Streams stdout/stderr with per-step/job/run progress; stop-on-first-failure with topological job ordering.
- **Run history** — Persists every run, job, and step to a local SQLite database with status, timing, exit codes, and trigger provenance.
- **Validation & listing** — Validates pipelines on-the-fly; lists all discovered pipelines with validity and errors.
- **Import existing workflows** — Convert GitHub Actions, GitLab CI, or Azure DevOps YAML into Railway pipeline format.
- **Dashboard** — Quick overview of repository, pipeline validity, and recent runs.
- **MCP server** — Local-only stdio MCP server exposing pipeline operations to AI assistants (Claude, Cursor, VS Code, etc.).
- **Graceful cancellation** — Ctrl+C cancels gracefully; second Ctrl+C forces termination. Detached hook runs are cleaned up on cancellation.

## Quick Start

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone the repository
git clone https://github.com/SimonNyvall/Railway.git
cd Railway

# Initialize Railway in a git repository
dotnet run --project Cli -- init

# Create your first pipeline
mkdir -p .railway/pipelines
cat > .railway/pipelines/build.yaml << 'YAML'
name: Build

jobs:
  build:
    steps:
      - name: Build
        run: dotnet build Railway.slnx --no-restore
YAML

# List pipelines
dotnet run --project Cli -- list

# Validate it
dotnet run --project Cli -- validate build.yaml

# Run it
dotnet run --project Cli -- run build.yaml

# Open dashboard
dotnet run --project Cli -- dashboard
```

> Note: Railway is not currently distributed as a `dotnet tool`. Run it via `dotnet run --project Cli -- <args>`.

## CLI Commands

| Command | Description |
|---|---|
| `railway init` | Initialize Railway in the current git repository (creates `.railway/`, manifest, and `pipelines/` dir). |
| `railway validate <FILE>` | Validate a pipeline YAML file (accepts relative path, absolute, or just filename). |
| `railway list` | List all pipelines under `.railway/pipelines/` with validity status and errors. |
| `railway run <FILE>` | Run a pipeline by filename. Auto-validates. Streams live output. |
| `railway dashboard` | Default command — shows repo name, pipelines, and last 10 runs. |
| `railway convert <PATH>` | Convert GitHub Actions/GitLab/Azure DevOps YAML to Railway format (`--format`, `--output`, `--force`). |
| `railway hooks install` | Install git hooks and OS scheduler entries for pipelines with triggers. |
| `railway hooks uninstall` | Remove Railway-managed hooks (`--purge` deletes logs). |
| `railway hooks status` | Show hook/scheduler state and per-pipeline trigger configuration. |
| `railway hooks sync` | Re-materialize hooks and report Added/Removed/Updated diff. |
| `railway hooks run <EVENT>` | Headless fan-out: trigger pipelines subscribed to a git/scheduler event (`--pipeline`, `--detach`, `--branch`, `--sha`, `--tag`, etc.). |
| `railway hooks watch` | Foreground file watcher running pipelines with `watch` triggers until Ctrl+C (`--debounce`, `--initial`). |

All commands that operate on a repo accept `--path <PATH>` (where applicable). Engine never relies on current working directory; it walks up from the resolved path to find `.railway`.

## Pipeline Schema

Pipelines live in `.railway/pipelines/*.yml` or `.*.yaml`. The manifest `.railway/railway.yaml` tracks pipeline names (CLI-maintained).

### Minimal example

```yaml
name: Build and Test

jobs:
  build:
    steps:
      - name: Restore
        run: dotnet restore Railway.slnx
      - name: Build
        run: dotnet build Railway.slnx --no-restore
```

### Full example with variables, dependencies, notify

```yaml
name: CI

variables:
  DOTNET_NOLOGO: "true"
  DOTNET_CLI_TELEMETRY_OPTOUT: "1"

notify: on-failure  # never | on-failure (default) | always

jobs:
  build:
    timeout: 00:10:00
    env:
      CONFIG: Release
    steps:
      - name: Restore
        run: dotnet restore
        timeout: 00:02:00
      - name: Build
        run: dotnet build --configuration Release --no-restore

  test:
    depends_on: [build]
    steps:
      - name: Test
        run: dotnet test --configuration Release --no-build --logger "console;verbosity=minimal"
```

**Notes:**
- `depends_on` references other job names; jobs run in topological order; execution stops on first failure.
- `timeout` is parsed for jobs/steps but **not enforced** in the current implementation (sequential execution).
- `locks` is parsed for jobs but **not enforced** currently.
- Environment merge order (later wins): process env → `pipeline.variables` → `job.env` → `step.env` (case-insensitive).

## Triggers & Hooks

Pipelines can declare `triggers:` to run automatically on git events, on a schedule, or when files change. `railway hooks install` materializes these into native git hooks and OS scheduler entries.

```yaml
name: AutoBuild
notify: on-failure

triggers:
  - post-commit
  - post-merge
  - post-checkout
  - push:
      branches: [main, "release/*"]
      tags: ["v*"]
  - schedule: "0 3 * * *"  # daily at 03:00
  - watch:
      - "src/**/*.cs"
      - "Tests/**/*.cs"

jobs:
  build:
    steps:
      - run: dotnet build Railway.slnx
```

| Event | Mechanism | Options |
|---|---|---|
| `post-commit` | git `post-commit` | — (detached/non-blocking) |
| `push` | git `pre-push` | `branches: [...]`, `tags: [...]` (wildcards allowed) |
| `post-merge` | git `post-merge` | — (forwards squash flag) |
| `post-checkout` | git `post-checkout` | `clone: true` (opt into running on initial clone/branch checkouts) |
| `schedule` | OS scheduler (launchd/crontab/schtasks) | Cron expression (5-field) or `@hourly/@daily/@weekly/@monthly/@yearly` |
| `watch` | `railway hooks watch` (foreground) | Glob paths; defaults to `["**/*"]` if empty |

**Hook management:**
- `railway hooks install` — Materialize wrappers in `.railway/hooks/`, write git hooks (honoring `core.hooksPath`/worktrees), manage `.gitignore` block, register schedulers. Foreign hooks are backed up as `<hook>.railway-backup` and chained.
- `railway hooks sync` — Diff and re-materialize; reports Added/Removed/Updated.
- `railway hooks status` — Inspect installed hooks, scheduler registrations, per-pipeline triggers, locks, last run.
- `railway hooks uninstall [--purge]` — Restore backups, remove Railway hooks; `--purge` deletes `.railway/logs/`.
- `railway hooks run <EVENT>` — Manually fire an event (useful for testing). Supports `--branch`, `--sha`, `--tag`, `--remote`, `--prev`, `--new`, `--flag`, `--is-squash`, `--cron`, `--detach`, `--no-notify`.
- `railway hooks watch` — Long-running file watcher; debounces changes and runs all pipelines with `watch` triggers.

**Environment variables (hooks/notifications):**
- `RAILWAY_HOOKS_NOTIFY` — Set to `0` to disable notifications.
- `RAILWAY_HOOKS_NOTIFY_PLATFORM` — Override platform detection (`macos|linux|windows`).
- `RAILWAY_HOOKS_SCHEDULER` — Force scheduler backend (`launchd|crontab|schtasks|fake`).
- `RAILWAY_HOOKS_FAKE_SCHEDULER_ROOT` — Root for fake scheduler entries (testing).
- `RAILWAY_HOOKS_LAUNCHD_ROOT` — Override launchd plist location.

## MCP Server (Local-only)

Railway ships an MCP server (`Railway.Mcp`) that exposes pipeline operations to AI assistants over stdio. No network listener, no auth.

### Tools

| Tool | Parameters | Returns |
|---|---|---|
| `list_pipelines` | `path?` | Discovered pipelines: `FileName`, `Path`, `Name`, `IsValid`, `Errors[]`. |
| `validate_pipeline` | `file`, `path?` | `Name`, `Errors[]` (empty = valid). `file` may be absolute, relative to `.railway/pipelines`, or repo root. |
| `run_pipeline` | `file`, `verbose?`, `includeOutput?`, `path?` | Blocks and streams progress. Returns `RunId`, `PipelineName`, `Status`, `ExitCode`, `Jobs[]` with `Steps[]`. |
| `dashboard` | `path?` | `RepositoryName`, `Pipelines[]`, `RecentRuns[]` (latest 10). |
| `runs` | `limit?` (default 50), `runId?`, `path?` | With `runId`: full `RunDetail`. Without: `RunSummary[]`. |

### Path resolution

1. Explicit `path` argument
2. `RAILWAY_REPO_PATH` environment variable
3. Process current working directory

Engine walks upward from the resolved path to find `.railway`.

### Run locally

```bash
dotnet run --project Mcp
```

### MCP client configuration (`.mcp.json`)

```json
{
  "mcpServers": {
    "railway": {
      "command": "dotnet",
      "args": ["run", "--project", "Mcp"],
      "env": { "RAILWAY_REPO_PATH": "/absolute/path/to/your/repo" }
    }
  }
}
```

Drop `env` if the client launches from inside the repo or you pass `path` per tool. Adjust `args` if running from a different working directory.

### Packaging

The MCP project is configured as `PackageType=McpServer`, `PackAsTool`, self-contained single-file. Build with `dotnet pack` to publish as a NuGet tool package; clients can launch via `dnx`.

See [Mcp/README.md](Mcp/README.md) for more details.

## Data & Storage

- **Database**: EF Core + SQLite. Auto-created on startup with schema upgrades.
- **Location**: Platform-specific app data dir — Windows `%LocalAppData%/Railway`, Linux `$XDG_DATA_HOME/Railway` or `~/.local/share/Railway`, macOS `~/Library/Application Support/Railway`.
- **Entities**: `Run`, `RunJob`, `RunStep` store full execution history; pipeline YAML files are the source of truth.

## Limitations

- `timeout` and `locks` are parsed but not enforced in the current implementation.
- CLI is not distributed as a global tool yet (use `dotnet run`).
- Some legacy schema upgrades run via `ALTER TABLE` shims (no EF migrations).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions, project structure, coding conventions, and how to run tests.

## License

MIT License — see [LICENSE](LICENSE). Copyright © 2026 NeotericSh.
