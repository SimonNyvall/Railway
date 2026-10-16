# Railway MCP Server

`Railway.Mcp` is a thin [Model Context Protocol](https://modelcontextprotocol.io) adapter over the
Railway Engine. It exposes the CLI's local CI features as MCP tools so an MCP client (Claude
Desktop, Cursor, VS Code, the MCP inspector, ...) can list, validate, run, and inspect pipelines in a
Railway repository.

## Local-only

This server is **local-only**:

- It uses the **stdio transport** — the client spawns the process and talks to it over stdin/stdout.
- There is **no network listener and no network exposure**.
- **No auth is required** — the client starts the process on your machine, so access is already
  scoped to your user. Do not expose this process over a socket or proxy.

> **stdout is reserved for the MCP protocol.** All logging goes to **stderr**
> (`LogToStandardErrorThreshold = LogLevel.Trace`). Never write diagnostics to stdout, or you will
> corrupt the protocol stream.

## Repository path resolution

Every tool takes an optional `path` argument. The target repository is resolved in this order:

1. The tool's explicit `path` argument.
2. The `RAILWAY_REPO_PATH` environment variable.
3. The process current working directory.

The resolved path is passed to the Engine unchanged; the Engine walks upward to find the `.railway`
folder, so any path inside the repo works.

## Tools

| Tool | Parameters | Returns |
|---|---|---|
| `list_pipelines` | `path?` | Pipelines discovered in the repo: `FileName`, `Path`, `Name`, `IsValid`, `Errors[]`. |
| `validate_pipeline` | `file`, `path?` | `Name`, `Errors[]`. An empty `Errors` list means the pipeline is valid. |
| `run_pipeline` | `file`, `verbose?`, `includeOutput?`, `path?` | Blocks until the pipeline finishes, streaming progress notifications. Returns `RunId`, `PipelineName`, `Status`, `ExitCode`, and `Jobs[]` (each with `Steps[]`). |
| `dashboard` | `path?` | `RepositoryName`, `Pipelines[]`, `RecentRuns[]` (latest 10 runs). |
| `runs` | `limit?` (default 50), `runId?`, `path?` | With `runId`: full `RunDetail` (jobs and steps). Without: a list of `RunSummary`. |

Notes:

- `validate_pipeline`'s `file` may be absolute, relative to `.railway/pipelines`, or relative to the
  repository root.
- `run_pipeline`'s `file` is a pipeline file name (matched by file name only). `verbose` includes each
  step's command in progress updates; `includeOutput` also streams stdout/stderr lines — expect a lot
  of messages for chatty commands, so leave it off unless you need it.
- Engine failures (not a Railway repo, not a git repo, missing/ambiguous/invalid pipeline, ...) are
  surfaced as clear MCP tool errors.

## Run locally

From the repository root:

```bash
dotnet run --project Mcp
```

Or point an MCP client at the project. The server speaks MCP over stdio, so it is configured by
spawning it, not by connecting to a URL.

## `.mcp.json` client snippet

```json
{
  "mcpServers": {
    "railway": {
      "command": "dotnet",
      "args": ["run", "--project", "Mcp"],
      "env": { "RAILWAY_REPO_PATH": "/abs/path/to/repo" }
    }
  }
}
```

`RAILWAY_REPO_PATH` is optional: drop it (or the whole `env` block) if you always pass `path`, or if
the client launches the process from inside the target repository. Adjust `args` if the client's
working directory is not the repository root (for example
`["run", "--project", "/abs/path/to/Railway/Mcp"]`).

## Packaging

The project is configured as an MCP server NuGet package (`PackageType=McpServer`, `PackAsTool`,
self-contained, single-file) with metadata in `.mcp/server.json`. Pack with `dotnet pack` and publish
to NuGet.org like any other tool package; clients can then launch it via `dnx`.

## Learn more

- [Model Context Protocol](https://modelcontextprotocol.io)
- [MCP C# SDK](https://csharp.sdk.modelcontextprotocol.io/)
