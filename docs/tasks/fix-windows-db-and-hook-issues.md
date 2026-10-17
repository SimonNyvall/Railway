# Fix: Windows DB schema failure, flaky CI hook tests, `hooks watch` positional path, status glyphs

Status: **approved design, not implemented**. Branch name agreed: `fix/windows-db-and-hook-issues` (one branch, tests + docs updated).

Context: `AGENTS.md` (root) describes the Engine/Cli/Mcp split, hook orchestration and test conventions. Read it first; this brief only adds what is specific to these four fixes.

## Goal

Four defects:

1. `railway run build-and-test.yaml` fails on Windows with an EF save error.
2. Two tests fail on GitHub CI (Linux) — `HooksWatchTests.Watch_Initial_RunsImmediatelyAndAgainOnChange` and `HookInstallerTests.Sync_WhenStaleHookHadBackup_RestoresTheBackup`.
3. `railway hooks watch ../README.md` (any positional) crashes with an unhandled `CommandParseException`.
4. "Recent Runs" status glyphs are too heavy/large; the Running symbol in particular.

## Verified findings

### 1. Windows failure is a **missing table**, not locking

Inner exception captured from the Windows machine (`--verbose` prints it via `Cli/Commands/RunCommand.cs:47-50`):

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 1: 'no such table: Runs'
  ... at Cli/Handlers/RunHandler.cs:25 -> Engine/Services/RunnerService.cs:32 (SaveChangesAsync)
```

Why:

- `Engine/EngineInitializer.cs:44-45` calls `EnsureCreatedAsync`, which is a **no-op when the DB file already exists** (stale/partial `%LocalAppData%\Railway\data.db` on that machine).
- `Engine/EngineInitializer.cs:72-77` (`EnsureRunProvenanceColumnsAsync`) deliberately returns when the `Runs` table is absent ("EnsureCreated owns schema creation — never mask a missing table with a failed ALTER"), so nothing repairs a partial schema.
- Model tables (from EF, verified by generating the create script): `Pipelines`, `Repositories`, `RepositoryPipelines`, `Runs`, `RunJobs`, `RunSteps`.
- Sightings of legacy singular names (`Repository`, `Run`) are why the existing shims probe `('repositories','repository')` / `('runs','run')` (`EngineInitializer.cs:228-283`).

Verified technical facts (probed with EF/Microsoft.Data.Sqlite 10.0.12):

- `context.Database.GenerateCreateScript()` returns **dependency-ordered** `CREATE TABLE "X" ( ... );` statements (plus `CREATE INDEX`), with no embedded semicolons — statements can be executed selectively in order to create only missing tables.
- `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 exposes **no** `EnableRetryOnFailure` and no transient-error detector / retrying execution strategy (assembly scan found none), so EF cannot retry SQLite failures for us.
- `Microsoft.Data.Sqlite` honours `DefaultTimeout` as a busy timeout: holder connection holding a write lock + `DefaultTimeout=1` → `SQLite Error 5 'database is locked'` after ~1.08 s. (`Microsoft.Data.Sqlite.Core` 10.0.12 is already in the graph transitively via `Microsoft.EntityFrameworkCore.Sqlite`.)

### 2a. `HooksWatchTests` — Linux inotify gap

- `Engine/Orchestration/FileSystemWatcherWatchEventSource.cs:26-41` wraps `System.IO.FileSystemWatcher` with `IncludeSubdirectories = true`; on Linux this is inotify-based and **not recursive**: the watch for a newly created directory is added only after the directory's own create event is processed, so a file written into a brand-new directory can be lost permanently. macOS (FSEvents) and Windows are recursive — hence CI-only.
- Test writes into a directory it just created (`Tests/Integration/HooksWatchTests.cs:158-168`), then asserts a second run after `--initial` (line 112-114).

### 2. `HookInstaller.RemoveStaleGitArtifacts` lazy-enumeration bug

`Engine/Orchestration/HookInstaller.cs:503-520`: `Directory.EnumerateFiles(.git/hooks)` is enumerated lazily while `post-commit` is deleted and `post-commit.railway-backup` is **moved onto** it (`File.Move(backupPath, hookPath, overwrite: true)`). The enumeration can still yield the now-gone `post-commit.railway-backup` entry, and line 505 calls `File.ReadAllText(hookPath)` with no existence check → `FileNotFoundException` (Linux `readdir` ordering exposes it; macOS ordering happened to avoid it).

`HookScriptHelper.BackupSuffix` = `.railway-backup` (`Engine/Helpers/HookScriptHelper.cs:35`).

### 3. `hooks watch` has no positional argument

- `Cli/Commands/HooksWatchSettings.cs` declares only options; there is **no** `[CommandArgument]`.
- Spectre 0.55.0 `CommandTreeParser.ParseString` throws `CommandParseException.UnknownCommand(..., "<token>")` when a string token remains and `node.HasArguments()` is false — so *any* stray positional (`../README.md`, `foo`) crashes, and the trace escapes to `Program.<Main>$` (`Cli/Program.cs:152`) because nothing catches `CommandParseException`.

### 4. Status glyphs

Hardcoded literals: `Cli/Handlers/DashboardHandler.cs:61-65` (Recent Runs), `:36,40,43` (Pipelines), `Cli/Rendering/HookRunResultRenderer.cs:37-53` (hooks run/watch). No test asserts the glyphs (`Tests/Integration/DashboardCommandTests.cs` only asserts the header).

## Decisions (all confirmed by the user)

### Fix 1 — schema verification + repair at init (Engine)

- After `EnsureCreatedAsync`, reconcile the DB against the EF model:
  - Missing **table** whose legacy name exists (`Repository`→`Repositories`, `Run`→`Runs`, `RunJob`→`RunJobs`, `RunStep`→`RunSteps`): `ALTER TABLE ... RENAME TO` to preserve data.
  - Otherwise create it from the EF-generated create script, executing statements **in script order** and skipping statements whose table already exists (keeps FK order). The script also carries `CREATE UNIQUE INDEX "IX_Repositories_Path"`; keep the existing index shim (`EnsureRepositoryPathUniqueIndexAsync`) as the authority and make sure the two don't conflict.
- Column reconciliation stays as-is: keep the targeted `Trigger`/`TriggerDetail` shim (`EngineInitializer.cs:70-103`) and the unique-index/dedup shim (`:112-197`). Do **not** build a full model-vs-sqlite_master reconciler now.
- Accepted limitation to record in comments/docs: legacy single-table names other than the four listed are not migrated; if a legacy table is renamed, its `sqlite_sequence`/indexes come along or are re-created by the existing shims.
- Keep `EngineOptions.InitializeDatabase` behaviour; this all lives under `InitializeAsync`.

### Fix 1b — extra hardening (user explicitly kept all three)

- **Busy timeout**: make it explicit in the connection string (`Engine/ServiceCollectionExtensions.cs:22-25` and the test/other connection-string sites) e.g. `Default Timeout=30`; document that Microsoft.Data.Sqlite maps it to `sqlite3_busy_timeout` (verified).
- **Retry**: EF's `EnableRetryOnFailure` is unavailable for SQLite (verified) → add a small Engine-side retry helper that retries `SaveChangesAsync` on transient SQLite errors (error codes 5 `SQLITE_BUSY`, 6 `SQLITE_LOCKED`) with a bounded backoff. Prefer detecting `Microsoft.Data.Sqlite.SqliteException.SqliteErrorCode` directly (add an explicit `Microsoft.Data.Sqlite.Core` 10.0.12 `PackageReference` to `Engine/Engine.csproj`) over the existing message-matching approach; note `RunnerService.IsUniqueConstraintViolation` (`Engine/Services/RunnerService.cs:399-414`) can then be simplified/kept consistent. Apply at the run/step/job save sites in `RunnerService` and `HookRunner` (no explicit transactions are used, so no execution-strategy wrapper is required).
- **Windows path normalization**: add a path canonicalizer (e.g. `Engine/Helpers/` helper: `Path.GetFullPath` + trim trailing separator, then lowercase on Windows only) and use it at every `Repository.Path` read/write site:
  - `Engine/Services/RunnerService.cs:365-396`
  - `Engine/Orchestration/HookRunner.cs:319-350`
  - `Engine/Orchestration/HookInstaller.cs:725-728`
  - `Engine/Services/RunHistoryService.cs:78-82`
  - `Engine/Services/DashboardService.cs:20-29`
  - startup dedup in `Engine/EngineInitializer.cs:126,138-208` — group/dedupe case-insensitively **on Windows only** so Linux keeps case-sensitive distinct repos.
  - `Repository.Path` is never rendered to users (display name is `DirectoryInfo.Name`), so lowercasing is not user-visible.

### Fix 2a — watcher rescans newly created directories

- `Engine/Orchestration/FileSystemWatcherWatchEventSource.cs`: when a `Created` event is a directory, after the OS watch has been armed (short delay, e.g. ~250 ms) walk that subtree and raise a `WatchPathChange` for each file found. `HookWatcher` already ignores `.git`/`.railway`/ignored paths and coalesces per-pipeline pending runs, so duplicate/synthetic events are harmless (`Engine/Orchestration/HookWatcher.cs:149-187`).
- Keep `HookWatcher` unchanged apart from whatever is needed for the seam; the event source stays internal.
- No periodic full-tree scan (rejected as unnecessary).
- Tests: keep `Watch_Initial_RunsImmediatelyAndAgainOnChange` writing into a newly created directory (it now exercises the fix) and add a deterministic case that writes into a directory that already existed before `Start`.

### Fix 2 — installer backup restore

In `Engine/Orchestration/HookInstaller.cs:503-520`:
- skip entries whose name ends with `HookScriptHelper.BackupSuffix` (they are restored with their hook, never evaluated on their own), and
- guard the read with `File.Exists` (or materialize the listing and re-check existence) so a deleted/moved entry can never throw.

Apply the same defensive `File.Exists`/read-guard to the wrapper loops at `:536-547` and `RemoveWrapperArtifacts` (`:567-580`) even though they do not currently move another entry.

### Fix 3 — `hooks watch` positional path + friendly parse errors

- `Cli/Commands/HooksWatchSettings.cs`: add `[CommandArgument(0, "[WATCH_PATH]")] public string? WatchPath { get; set; }`.
- `Cli/Commands/HooksWatchCommand.cs`: resolve the repo path as `settings.Path ?? settings.WatchPath`; if **both** are supplied with different values, print a clear error and exit 1 (do not silently prefer one).
- `Cli/Program.cs:150-153`: catch `Spectre.Console.Cli.CommandParseException` and print `Error: <message>` (no stack trace), exit 1, replacing today's unhandled-exception output.
- Docs: `README.md:76` and `docs/tasks/pipeline-hooks.md:35` — document `hooks watch [WATCH_PATH]`.

### Fix 4 — lighter status glyphs

Apply everywhere (dashboard Pipelines + Recent Runs, `HookRunResultRenderer` for hooks run/watch/watch):

| Status | Now | New |
| --- | --- | --- |
| Succeeded | `✓` | `✓` (unchanged) |
| Failed | `✗` | `×` |
| Running | `⟳` | `↻` |
| Cancelled / Skipped | `⊘` | `–` |

Colors stay as they are. No glyph assertions exist in tests; add none unless a test naturally covers the status text.

## Acceptance criteria

- Running `railway run build-and-test.yaml` against a DB that is missing `Runs` (or any model table) initializes the missing tables and completes instead of throwing `SQLite Error 1: 'no such table: Runs'`.
- A legacy DB whose history lives in singular-named tables keeps that history after init (rows are readable through the plural model tables).
- `dotnet test` passes on Linux: both CI failures above are fixed, and the watch suite is not flaky (new-directory writes are detected).
- `railway hooks watch ../README.md` watches the resolved repository (no `CommandParseException`); `hooks watch --path X Y` (conflicting) fails with a clear message; any other bad argument prints `Error: …` + exit 1 instead of a stack trace.
- Recent Runs / hooks output show the new glyph set.
- Windows-vs-Linux repository path variants no longer create duplicate `Repositories` rows on Windows.

## Verification steps

1. `dotnet build Railway.slnx` and `dotnet test` (AGENTS.md: build + test only, no app runs).
2. New regression tests:
   - **partial-schema init**: create a DB containing only a legacy subset (e.g. `Pipelines`+`Repositories`, optionally singular `Run`/`Repository` with rows), run `InitializeEngineAsync`, assert all model tables exist, a run persists, and history queries return the pre-existing rows for the rename case. Prefer extending `Tests/Integration/` (see `Tests/Infrastructure/TestDatabaseFixture.cs`) and the existing `EngineInitializer`/DB tests.
   - **watcher**: new-directory write (existing test) + pre-created-directory write (new test) in `Tests/Integration/HooksWatchTests.cs`.
   - **installer**: existing `Sync_WhenStaleHookHadBackup_RestoresTheBackup` (`Tests/Unit/HookInstallerTests.cs:409`) must pass; consider a case with a backup suffixed file present and no matching hook.
   - **CLI**: positional `hooks watch <path>` binds; conflict with `--path` errors; parse error handled (see `Tests/Integration/HooksCommandTests.cs:66` for how commands are registered in tests).
3. Re-run the specific failing tests with `--filter` locally and confirm no `FileNotFoundException`/`Unknown command`.

## Open items / risks

- Implementation detail left to the implementer: extracting individual `CREATE TABLE` statements from `GenerateCreateScript()` (verified: dependency-ordered, statements end with `);`, no embedded `;`). Guard against a future model change altering that shape.
- The Windows machine's DB table list was never inspected (user opted to skip diagnostics); the repair must therefore handle both "missing plural table" and "legacy singular table with data" paths.
- Schema repair runs on every init (cheap: a few `sqlite_master` queries + no-op script filtering); confirm no measurable startup cost.
- `EngineInitializer` remains SQLite-specific raw ADO; keep the wrapper injectable so no direct `Microsoft.Data.Sqlite` dependency leaks beyond `Engine/Services`/`Infrastructure` if avoidable — but an explicit `Microsoft.Data.Sqlite.Core` reference is approved for the retry helper.

## References

- Root guide: `AGENTS.md`
- Hook feature spec: `docs/tasks/pipeline-hooks.md` (watch behaviour line 35, SQLite contention A6 line 92, Phase 4 at line 174)
- README command table: `README.md:74-78`, 165-173
- CI workflow: `.github/workflows/build-test.yml`
