# Task: Pipeline hooks — git triggers, schedules, and a file watcher

## Goal
Let pipelines run themselves instead of you typing `railway run <file>` all day. Pipelines declare
**triggers** in their own YAML (`post-commit`, `push`, `post-merge`, `post-checkout`, `schedule`,
`watch`); `railway hooks install` materializes them into **native git hooks** and **OS scheduler
entries** (launchd / crontab / schtasks); a thin generated wrapper invokes the CLI headlessly. Every
hook-triggered run is recorded in run history with its trigger, and failures notify you.

Engine stays host-agnostic: `IPipelineRunner.RunAsync(repo, file, verbose, progress: null, ct)` is
already a silent, headless entry point (proven by the MCP host). Hooks are a **new host concern**, not
a new execution engine.

## Decisions (confirmed with user)
| Topic | Decision |
|---|---|
| Trigger families (v1) | Git commit hook, cron-style schedules, push (+ branch/tag filters), post-merge, post-checkout, file watcher |
| Config location | Per-pipeline block inside `.railway/pipelines/*.yaml` (no new config file, `railway.yaml` untouched — its pipeline index is currently never read) |
| Schema shape | One `triggers:` list; entries are either a scalar event name or a single-key map with options |
| Execution model | **Hybrid**: git hooks = real `.git/hooks` scripts (git must invoke them); schedules = OS-native (launchd on macOS, crontab on Linux, schtasks on Windows). No resident daemon |
| Commit hook | `post-commit` only, **non-blocking**: commit always succeeds, pipeline runs detached |
| Push / merge / checkout | Non-blocking + opt-in per pipeline; `push` maps to git `pre-push` and never aborts the push; `post-checkout` fires only for branch checkouts (`flag=1`), never for file-level checkouts; `clone` excluded unless explicitly opted in |
| Backgrounding | `railway hooks run <event> --detach`: the CLI spawns a detached child and returns immediately (scripts stay identical on every platform, incl. Git-for-Windows) |
| Repo path | `--path` only on `run` and the `hooks` commands (not global); generated scripts also `cd` to the repo root |
| Provenance | `Run.Trigger` (`manual\|hook\|schedule\|watch`) + `Run.TriggerDetail` (branch/sha/cron/glob), added via a startup `ALTER TABLE` shim |
| Overlap policy | Per repo+pipeline lock file; if held → **skip** and record a `Run` row with new `RunStatus.Skipped` (no queueing) |
| Skip visibility | Skipped triggers appear in dashboard/MCP run history |
| Notifications | `notify: never\|on-failure\|always` per pipeline (default `on-failure`); `--no-notify` flag and `RAILWAY_HOOKS_NOTIFY=0` kill switch; native one-liners with graceful degradation (macOS `osascript`, Linux `notify-send`, Windows PowerShell toast) |
| Existing git hooks | Chain + backup: honor `git rev-parse --git-path hooks` (worktrees, `core.hooksPath`); foreign hook moved to `<hook>.railway-backup` and chain-called |
| Artifacts | Generated wrapper scripts in `.railway/hooks/`, logs in `.railway/logs/`; both added to `.gitignore` by `install` (see Assumption A1) |
| Cron parsing | Own minimal parser in Engine (5-field + `@hourly/@daily/@weekly/@monthly`), no new dependency; provides validation, next-occurrence, and field sets |
| Cron → launchd gaps | Expand to `StartCalendarInterval` arrays when representable; otherwise `install`/`sync` **fails loudly** with pipeline + expression + suggested equivalent. Never silently drop a schedule |
| CLI surface | `railway hooks install\|uninstall\|status\|sync\|watch` + `hooks run <event>` (nested branch) |
| Testability | New narrow seams: `IScheduler` (launchd/crontab/schtasks + fake) and `ISystemCommandRunner`; git-hook install tested against real temp git repos |
| Watch behavior | `hooks watch [--pipeline <file>] [--debounce 1s] [--initial]`: foreground until Ctrl+C, one process handles all watch triggers, ignores `.git`, `.railway`, gitignored paths, `bin/obj/node_modules`, no initial run unless `--initial` |
| Phasing | P1 commit hook end-to-end → P2 schedules → P3 push/merge/checkout → P4 watcher |
| MCP | Untouched (no new MCP tools in v1) |

## Schema (v1)
```yaml
name: Build and Test
notify: on-failure            # never | on-failure | always (default: on-failure)
triggers:
  - post-commit
  - post-merge
  - post-checkout
  - push:
      branches: [main]
      tags: ["v*"]
  - schedule: "0 3 * * *"      # 5-field cron or @daily/@hourly/@weekly/@monthly
  - watch: ["src/**/*.cs", "Tests/**/*.cs"]
jobs:
  ...
```
Rules:
- Entry = scalar (`post-commit`) or single-key map (`push: {…}`, `schedule: "…"`, `watch: […]`). Map form is
  allowed for any event; only `push`, `schedule`, `watch` accept options.
- A `--pipeline`-agnostic event fans out to **all** pipelines that subscribe to it, in discovery order.
- `schedule` requires exactly one expression; multiple schedules = multiple entries.
- `watch` takes a glob or list of globs (non-empty). Default when the map value is empty/null:
  `["**/*"]` minus the ignored set.
- **Deliberately not `on:`** — YAML 1.1 parses a bare `on` key as boolean, and `PipelineConvertService.DetectFromContent`
  keys on the literal `"on"` for GitHub detection (`Engine/Services/PipelineConvertService.cs:138`).
- Duplicate event entries in one pipeline → validation error.

## Verified facts (from repo)
- **Headless entry point exists**: `IPipelineRunner.RunAsync(startPath, fileName, verbose, IProgress<RunEvent>? = null, ct)` (`Engine/Orchestration/Interfaces/IPipelineRunner.cs`, impl `Engine/Orchestration/PipelineRunner.cs:24-85`); `progress` is nullable everywhere and `RunnerService` persists all step output regardless — `progress: null` = fully silent run. Second precedent for a non-Spectre sink: `Mcp/Tools/RunEventProgressAdapter.cs`.
- **Pipeline identity = file name** (`PipelineRunner` matches `Path.GetFileName(p.FileName) == fileName`); duplicates anywhere → `AmbiguousPipelineException`. Discovery scans `.railway/pipelines/` recursively for `.yml/.yaml` (`Engine/Services/PipelineDiscoveryService.cs`).
- **Parse/serialize**: `Engine/Helpers/PipelineHelper.cs` — YamlDotNet, `UnderscoredNamingConvention`, `IgnoreUnmatchedProperties`. Write path uses `PipelineWriteModel`/`JobWriteModel`/`StepWriteModel` (`:97`, `:113`, `:135`) with `OmitDefaults`-style emission — **new keys must be added there or `convert`/round-trips drop them**. Parse catches broad `Exception` in `TryParse` (deliberate).
- **EF mapping**: `Pipeline.Jobs`/`Variables` are EF-ignored in `RailwayDbContext.OnModelCreating` (`Engine/Infrastructure/RailwayDbContext.cs:34-35`); new `Pipeline.Triggers`/`Notify` need the same treatment.
- **DB**: EF Core Sqlite, `EnsureCreatedAsync` only, **no migrations/schema version** (`Engine/EngineInitializer.cs:32-46`); DB lives in the per-OS app-data dir, keyed per repo by absolute path (`Engine/Helpers/DatabasePathHelper.cs`); `DatabasePathHelper.GetForPlatform` is the only existing OS seam (`InternalsVisibleTo("Tests")`).
- **`RunStatus`**: `Succeeded = 0` (uninitialized default gotcha), then `Failed`, `Running`, `Cancelled` (`Engine/Domain/RunStatus.cs`). Only behavioral switch site is `Cli/Handlers/RunHandler.cs:71` (`if (run.Status == RunStatus.Succeeded) … else throw "Pipeline failed…"`); rendering site is `Cli/Handlers/DashboardHandler.cs:59-66` (has a `_ =>` fallback); MCP uses `.Status.ToString()` (`Mcp/Tools/ToolDtoMapper.cs`) → no compile breaks, but a `Skipped` run must not be reported as a failure.
- **Run creation**: `RunnerService.Run` builds the `Run` row (`Engine/Services/RunnerService.cs:26-31`) from a `RunRequest` (`Engine/Orchestration/RunRequest.cs`); `PipelineRunner` constructs it. Provenance therefore flows **through `RunRequest`**.
- **No concurrency guard anywhere**: `locks`/`timeout` in pipeline YAML are parsed but never enforced (`Engine/Domain/Job.cs:7,9`; zero references in `RunnerService`); concurrent runs each insert their own `Run` row.
- **Process spawning** is `private static ExecuteStep` (`RunnerService.cs:232-342`) — not injectable/mockable; reuse `IPipelineRunner`, not this.
- **No CLI path helper**: every handler calls `Directory.GetCurrentDirectory()` inline (`Cli/Handlers/{Init,Run,Dashboard,List}.cs`); `ValidationHandler` takes the arg path. `init`/`list`/`dashboard` have **no settings class** (`Cli/Commands/{Init,List,Dashboard}Command.cs`), so a *global* `--path` would touch every command + settings class + handler interface — hence the narrow `--path` decision.
- **No global/user config** of any kind. Env vars in the solution: `RAILWAY_REPO_PATH` (MCP only), `XDG_DATA_HOME` (DB path), plus wholesale process-env inheritance into steps (`RunnerService.cs:202`).
- **CLI is not a dotnet tool**: `Cli/Cli.csproj` is a plain `Exe` named `Cli` (no `PackAsTool`/`ToolCommandName`); no `railway` binary on PATH; `SetApplicationName("railway")` is cosmetic (`Cli/Program.cs:46`). Only `Mcp/Railway.Mcp.csproj` is packed (`PackageType=McpServer`).
- **Spectre.Console.Cli 0.55.0** supports `AddBranch` (documented in the installed package XML) — **no `AddBranch` usage exists anywhere yet**; `Program.cs:49-67` registers five flat `AddCommand`s plus `CommandApp<DashboardCommand>` as default.
- **Cancellation is not wired in the CLI**: `Program.cs:75` calls `app.RunAsync(args)` with no token and there is no `Console.CancelKeyPress` handler; Engine-level `ct` support exists but is unused by the CLI (`Cli/Handlers/ConvertHandler.cs:96` passes `CancellationToken.None`).
- **`railway init` creates** `.railway/`, `.railway/pipelines/`, `.railway/railway.yaml` — it does **not** touch `.gitignore` and does not commit (`Engine/Orchestration/RepositoryInitializer.cs:18-49`). `.gitignore` (481 lines) has no `railway` entry.
- **Engine deps**: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.Extensions.DependencyInjection`, `YamlDotNet` — no cron/glob/watcher libraries.
- **Test harness**: `Tests/Infrastructure/TestFixtures.cs` (`TempGitRepository` = real temp dir + real `git init`; `TestRailwayRepository` writes `.railway/pipelines` manually; `TestDatabaseFixture`), `TestTypeRegistrar` mirrors the CLI registrations, integration tests serialized via `[Collection("Integration Tests")]`; no filesystem/process abstraction exists today.
- **Engine layering convention**: `Orchestration/Interfaces/I*` (explicit paths, no cwd, no Spectre, returns records / throws domain exceptions) → `Services/Interfaces/I*Service`; all registered in `Engine/ServiceCollectionExtensions.AddEngine`.

## Assumptions / verify during implementation
- **A1 — gitignore scope**: the accepted decision said `.railway/logs/` goes into `.gitignore`; wrapper scripts under `.railway/hooks/` embed a **machine-specific absolute launcher path**, so they should be ignored too. Plan: `install` appends one marker block to `.gitignore` covering `.railway/hooks/` and `.railway/logs/`; `uninstall` removes exactly that block. Confirm before shipping.
- **A2 — launcher capture**: with `dotnet run`, `Environment.ProcessPath` is the `dotnet` host and the assembly location is `Cli.dll` → record `dotnet "<abs>/Cli.dll"`; when a native apphost is used, record that path verbatim. Confirm both shapes in tests.
- **A3 — Spectre branch settings**: inner commands of an `AddBranch` must accept the branch settings type (or derive from it). Confirm the exact binding model in 0.55.0 and that shared `--path`/`--verbose` flow to subcommands.
- **A4 — launchd semantics**: within one `StartCalendarInterval` dictionary the keys are ANDed, across array entries they are ORed, so cron's `dom OR dow` rule needs `|dom| + |dow|` entries (Cartesian expansion where cron ORs). Verify against `man launchd.plist` and a real loaded job.
- **A5 — legacy-schema test**: simulating a pre-shim DB means creating the `Run` table without the new columns by raw SQL (or `ALTER TABLE Run DROP COLUMN`) before calling `InitializeEngineAsync`. Confirm `DROP COLUMN` availability in the bundled SQLite.
- **A6 — SQLite contention**: detached hook runs and scheduled runs are separate processes writing the same app-data DB; per-pipeline locking does not prevent cross-pipeline overlap. Plan: set a busy timeout on the connection string and verify a two-process run pair.
- **A7 — Git-for-Windows**: git hooks execute through `sh`; wrapper scripts must be POSIX `sh` compatible, and the schtasks path must not assume a POSIX shell.

---

## Phase 1 — Commit hook end-to-end (ship first)

### Step 1.1 — Engine: trigger domain + parsing
1. `Engine/Domain/TriggerEvent.cs` — enum `PostCommit, PrePush, PostMerge, PostCheckout, Schedule, Watch` (YAML keywords: `post-commit`, `push`, `post-merge`, `post-checkout`, `schedule`, `watch`).
2. `Engine/Domain/TriggerDefinition.cs` — `TriggerEvent Event`, `IReadOnlyList<string> Branches`, `Tags`, `WatchPaths`, `string? Cron`, `IReadOnlyList<string> Errors`.
3. `Pipeline` gains `List<TriggerDefinition> Triggers` (default empty) and `NotificationLevel Notify` (`Engine/Domain/NotificationLevel.cs`: `OnFailure = 0` default, `Never`, `Always`) — both **EF-ignored** in `RailwayDbContext.OnModelCreating` alongside `Jobs`/`Variables`.
4. `PipelineHelper`: parse `triggers:`/`notify:` via a YamlDotNet type converter (custom `IYamlTypeConverter` or a wire model handling scalar-or-map entries) into `TriggerDefinition`; add `Triggers`/`Notify` to `PipelineWriteModel` so `convert` and re-serialization keep them.
5. **Acceptance:** round-trip test — parse a pipeline with all six trigger forms, re-`Write`, re-`Parse`, deep-equal; a legacy pipeline without `triggers:` still parses (no regression, `IgnoreUnmatchedProperties` unchanged).
6. **Review notes (1.1, done):** build clean (0 warnings) and full suite green (274 tests, incl. 11 new in `Tests/Unit/TriggerDefinitionParsingTests.cs`); all six trigger forms round-trip and legacy pipelines write unchanged. Open smell (deferred): the event→keyword map is duplicated in `PipelineHelper.TriggerListYamlConverter` (`EventsByName`/`NamesByEvent`) and `HookScriptHelper.KeywordsByEvent`; consolidate onto one canonical accessor (`HookScriptHelper.GetEventKeyword`) once steps 1.1/1.2 settle, to avoid cross-step coupling mid-flight.

### Step 1.2 — Engine: cron parser
1. `Engine/Helpers/CronExpressionHelper.cs`: parse/validate 5-field cron (`*`, `N`, `a-b`, `a-b/s`, lists) + `@hourly/@daily/@weekly/@monthly`; expose field sets (`Minutes/Hours/DaysOfMonth/Months/DaysOfWeek`), `GetNextOccurrence(DateTime from, TimeZoneInfo)` (local time), and `ExpandForLaunchd()` returning the OR/AND entry list (fails loudly for `@reboot`, seconds fields, `L`/`W`/`#`).
2. **Acceptance:** unit tests for validation, next-occurrence, and dom-OR-dow expansion; explicit failure cases.

### Step 1.3 — Engine: provenance + skipped status
1. `Run.Trigger` (string, ≤20) + `Run.TriggerDetail` (string?, ≤200); `RunStatus.Skipped`.
2. `RunRequest` gains `Trigger`/`TriggerDetail` (default `manual`); `PipelineRunner.RunAsync` gains an optional `RunTrigger` parameter (record with `Key`/`Detail`) so `Cli/Handlers/RunHandler.cs` and MCP keep working unchanged.
3. `EngineInitializer`: startup shim — inspect `PRAGMA table_info(Run)`; when `Trigger`/`TriggerDetail` are missing run `ALTER TABLE Run ADD COLUMN …` (`Trigger` with `DEFAULT 'manual'`). Existing DBs keep working; no migration framework.
4. **Acceptance:** `Tests/Unit/EngineInitializerTests.cs` extended — fresh DB has the columns; legacy-shaped DB gets upgraded in place; `InitializeDatabase = false` still skips everything.

### Step 1.4 — Engine: hook-run orchestration
1. `Engine/Orchestration/Interfaces/IHookRunner.cs` + `Engine/Orchestration/HookRunner.cs`:
   `RunEventAsync(string repositoryPath, TriggerEvent event, HookEventContext ctx, string? pipelineFile, bool detach, bool notify, ct)` → `HookRunOutcome { IReadOnlyList<HookRunResult> Results, bool AnyFailed, bool AnySkipped }`, where `HookRunResult { PipelineFileName, RunId?, Status, Skipped, Error }`.
   - Resolve repo (`RepositoryHelper.GetRailwayPathOrThrow`), discover pipelines, select those with a matching trigger (and matching branch/tag for `push`), fan out in discovery order.
   - Per pipeline: acquire `HookLock` (see 1.5); if held → insert a `Run` row with `Status = Skipped`, `Trigger`, `TriggerDetail`, `StartedAt = FinishedAt = now`, `ExitCode = null`, **no** `RunJob`/`RunStep` rows; report skipped.
   - Otherwise `IPipelineRunner.RunAsync(repo, file, verbose: false, progress: <file sink>, ct)` with trigger provenance; write a per-run log under `.railway/logs/`.
   - Never throws for pipeline-level failures (returns results); throws domain exceptions only for repo-resolution problems.
2. `Engine/Orchestration/Interfaces/IHookInstaller.cs` + `HookInstaller.cs` (install/uninstall/status/sync for the git-hook half in P1): detect launcher, compute referenced events, write wrapper scripts, patch `.gitignore`, write/remove git hook files with markers, restore backups, report drift.
3. `Engine/Infrastructure/ISystemCommandRunner.cs` + `SystemCommandRunner.cs` (runs `git rev-parse --git-path hooks`, `launchctl`, `crontab`, `schtasks`; injectable, fakeable) and `Engine/Helpers/HookPaths.cs` (resolves repo-local `.railway/hooks`, `.railway/logs`, app-data lock root; all inputs injectable, no cwd reads).
4. `Engine/Helpers/HookScriptHelper.cs` — script templates (POSIX `sh`), markers (`# railway-managed hook: <event>`), chain/backup logic, marker-comment conventions for scheduler entries.
5. Register everything in `AddEngine`.
6. **Acceptance:** unit tests with fakes — event→pipeline selection, branch/tag filtering, skip-on-lock producing a `Skipped` row (no job/step rows), detach invocation shape, `.gitignore` block idempotency, foreign-hook chain + backup, status drift detection.

### Step 1.5 — Concurrency lock
1. `Engine/Helpers/HookLock.cs`: atomic create of `<lockRoot>/<repoSlug>/<pipelineSlug>.lock` containing pid + timestamp; stale when the pid is dead or older than a threshold (default 6 h); release in `finally`.
2. **Acceptance:** unit tests for acquire/contend/stale; skipped run recorded, never thrown.

### Step 1.6 — CLI: `run --path` + `hooks` branch
1. `Cli/Commands/RunCommand.cs` — add `--path`; `IRunHandler.Handle(fileName, verbose, path)` passes it through (`Cli/Handlers/RunHandler.cs:26` stops using `Directory.GetCurrentDirectory()` when `--path` is given).
2. `Cli/Program.cs` — `config.AddBranch<HooksSettings>("hooks", b => { b.AddCommand<HooksInstallCommand>("install") … })` for `install`, `uninstall`, `status`, `sync`, `run` (P1) and `watch` (stub in P1, implemented in P4). Mirror in `Tests/Infrastructure/TestTypeRegistrar.cs:49-63`.
3. `Cli/Commands/Hooks*Command.cs` + `Cli/Handlers/Interfaces/IHooks*Handler.cs` + `Cli/Handlers/Hooks*Handler.cs` (renderers):
   - `install` → green ✓ per installed hook/schedule, warnings for chained foreign hooks, error `exit 1` when nothing is declared.
   - `uninstall` → removes only marker-owned artifacts; `--purge` also deletes logs; warns (never deletes) a hook file that is no longer ours.
   - `status` → per pipeline: triggers, validation errors, git hook state (missing / installed / foreign-chained / drifted), schedule state (P2), next occurrence, last run, lock state.
   - `sync` → re-materialize from YAML, drop stale entries, report the diff.
   - `run <event> [--pipeline <file>] [--path] [--detach] [--no-notify] [--verbose]` → headless; **exit 0** when everything succeeded/skipped, **1** when any run failed; `--detach` spawns the detached child via `ISystemCommandRunner` and returns 0 immediately.
4. `Cli/Handlers/RunHandler.cs:71` — add explicit handling so `RunStatus.Skipped` is never rendered as a pipeline failure; `Cli/Handlers/DashboardHandler.cs:59-66` — add a `Skipped` case (dim) and append the trigger label to the Recent Runs line.
5. **Acceptance:** CLI integration tests — `hooks install` in a temp repo writes the hook + wrapper and reports; `hooks run post-commit` runs only subscribed pipelines and records `Trigger = hook`; `hooks status` shows drift after editing the YAML.

### Step 1.7 — Notifications (macOS path only in P1)
1. `Engine/Services/Interfaces/INotificationService.cs` + `NotificationService.cs` — `NotifyAsync(title, message, level)`; per-OS one-liners through `ISystemCommandRunner` (`osascript` / `notify-send` / PowerShell toast), log-a-warning-and-continue on failure; suppressed by `notify: never`, `--no-notify`, `RAILWAY_HOOKS_NOTIFY=0`.
2. **Acceptance:** unit tests with a fake runner assert the exact command per platform and the suppression matrix.

### Step 1.8 — Phase 1 tests
1. `Tests/Unit/` — `TriggerDefinitionParsingTests`, `CronExpressionHelperTests`, `HookRunnerTests`, `HookInstallerTests`, `HookLockTests`, `NotificationServiceTests`, plus the existing `EngineInitializerTests` extension.
2. `Tests/Integration/HooksCommandTests.cs` (real temp git repo, temp SQLite, `[Collection("Integration Tests")]`) — install → real `git commit` fires the hook → run recorded with `Trigger = hook`; detach disabled in tests (`--detach=false` or a `RAILWAY_HOOKS_*` override) for determinism; foreign hook preserved and still executed; uninstall restores the original hook and cleans `.gitignore`.
3. **Acceptance:** `dotnet test` green; no changes needed to existing tests beyond the `IRunHandler` signature.

---

## Phase 2 — Schedules (launchd / crontab / schtasks)
1. `Engine/Services/Interfaces/IScheduler.cs` (`List/Install/Remove` + `IsSupported`) with `Engine/Services/Schedulers/{LaunchdScheduler,CrontabScheduler,SchtasksScheduler}.cs`; selection by `RuntimeInformation.IsOSPlatform` with an override env var (`RAILWAY_HOOKS_SCHEDULER=launchd|crontab|schtasks|fake`) so tests never touch the real machine.
2. Schedule materialization: one entry per (pipeline, cron), invoking `<wrapper> hooks run schedule --pipeline <file> --path <repo>`; stdout/stderr to `.railway/logs/`; PATH-safe wrapper (cron has a minimal environment).
   - launchd: `~/Library/LaunchAgents/com.railway.<repoSlug>.<pipelineSlug>.plist`, `RunAtLoad = false`, `StartCalendarInterval` expanded per 1.2, load/unload via `launchctl bootstrap/bootout`.
   - crontab: marker-delimited block (`# railway: <repoSlug>:<pipelineSlug>`) read/written through `crontab -l` / `crontab -`.
   - schtasks: `schtasks /Create /TN "Railway\<repoSlug>\<pipelineSlug>" …` with a documented cron→`/SC` mapping; unmappable expressions fail loudly.
3. `hooks install` also materializes schedules; `hooks sync` reconciles (adds/removes/updates) and `hooks status` reports drift + next occurrence; `hooks uninstall` removes only marker-owned entries. Schedules always run in local time.
4. **Acceptance:** unit tests assert exact plist/crontab/schtasks content and the unmappable-expression failure path; integration test with the fake scheduler writes to a temp root; `hooks status` reports drift after a cron edit.

## Phase 3 — Push / post-merge / post-checkout
1. Extend `HookInstaller` to install `pre-push`, `post-merge`, `post-checkout` wrappers; extend `HookRunner` with event context parsing (remote/refs from `pre-push` stdin, `is_squash` for `post-merge`, `prev/new/flag` for `post-checkout`) and branch/tag filtering.
2. `post-checkout` fires only when `flag == 1` (branch checkout); clone excluded unless a pipeline opts in. `pre-push` never blocks: the wrapper returns immediately (detached), preserving git's expected hook contract.
3. `TriggerDetail` carries the human-readable context (`branch=main`, `sha=abc1234`, `tag=v1.2`, `remote=origin`).
4. **Acceptance:** integration tests in a temp repo — commit, branch switch, merge, and a local push to a bare temp remote each fire the right subset of pipelines; unrelated events do not; a failing pipeline never blocks git.
5. **Review notes (3, done):** build clean (0 warnings) and full suite green (523 tests). Deferred step-1.1 smell resolved: `PipelineHelper.TriggerListYamlConverter.EventsByName`/`NamesByEvent` now derive from `HookScriptHelper.GetEventKeyword`, so the YAML vocabulary can no longer drift from the keywords the wrappers emit. Test-only duplication removed (`HooksGitEventsTests` runs every git command through one working-directory-aware helper).
   - **Gaps (accepted for v1):** (a) a chained foreign `pre-push` hook receives empty stdin because the railway wrapper drains git's ref lines before the chain-call in `BuildGitHookScript`; low impact (rare to chain `pre-push`) and inherent to "own the hook, call the backup after". (b) ~~`HookRunner.RunEventAsync` reports `Pipeline '<file>' was not found.` when `<file>` exists but no trigger matches the event — misleading, but no test pins the message.~~ **Fixed:** `RunEventAsync` now distinguishes a missing file (`was not found`) from an existing file with no matching trigger (`has no matching trigger for event <keyword>`); pinned by `HookRunnerTests`. (c) `HookPipelineSelector.MatchesWildcard` compiles a `Regex` per candidate; fine at hook fan-out scale, cache only if pipeline counts grow.

## Phase 4 — `hooks watch`
1. `Engine/Orchestration/Interfaces/IHookWatcher.cs` + `HookWatcher.cs` — recursive `FileSystemWatcher` on the repo root, glob→regex matching, ignore set (`.git`, `.railway`, gitignored paths via `git check-ignore`, `bin/obj/node_modules`), 1 s debounce per pipeline, run through `IHookRunner` with `Trigger = watch`.
2. Cli: wire `CancellationTokenSource` on `Console.CancelKeyPress` and pass the token through `CommandApp.RunAsync(args, token)` → handler → runner, so Ctrl+C cancels cleanly and releases any lock. Wire the token for `hooks run` too.
3. **Acceptance:** unit tests for glob/ignore/debounce decisions with a fake watcher; integration test drives the watcher through a temp file write (or a test-only input path) and asserts exactly one run per debounced burst, two when `--initial` is passed.
4. **Review notes (4, done):** build clean (0 warnings) and full suite green (523 tests, incl. `Tests/Unit/HookWatcherTests.cs` — glob/ignore/debounce/initial/filter/cancellation via a fake `IWatchEventSource` — and `Tests/Integration/HooksWatchTests.cs` — real `FileSystemWatcher` + temp repo/DB). `Tests/Integration/HooksCommandTests.cs` was also updated: the old `watch`-is-a-stub assertion was replaced by an invalid-`--debounce` exit-code test (that file is not owned by another phase). Remediation B (watcher/lock): (a) `HookWatcher` report loop is guarded so a throwing progress sink never kills the loop; `HooksWatchHandler` escapes markup in user-controlled output. (b) glob→regex hardened (character-class sanitization, bounded regex timeout, malformed/pathological patterns degrade safely). (c) `HookLock` stale-break made atomic (file-move claim), catch filter broadened; a striped in-process lock added to reduce in-process race surface. Open smells (deferred, out of this step's file ownership): (d) `Cli/Handlers/HooksWatchHandler.RenderResult` duplicates `Cli/Handlers/HooksRunHandler.RenderResult` — only the timestamp prefix and indentation differ; extract a shared `HookRunResult` renderer. (e) `Engine/Orchestration/HookWatcher.cs` co-locates the watch loop, glob translator, and `FileSystemWatcher` seam; splitting would improve readability.

---

## Known issues / follow-ups (post-implementation)
- **L2 (Process lifecycle):** ✅ **Fixed** — Spawned detached processes are tracked in `ISpawnedProcessTracker`/`SpawnedProcessTracker` (singleton). Detached processes run in their own process group on Windows (`CreateNewProcessGroup=true`). On Ctrl+C, the CLI unregisters handlers but also kills tracked process trees; on cancellation during step execution, `RunnerService` kills the step's process tree and bounds reader drain. Trees are killed via `ProcessTreeKiller` with best-effort cleanup.
- **L3 (Repository identity):** ✅ **Fixed** — Added unique index on `Repository.Path` in EF model, startup schema shim that repairs/deduplicates existing rows (re-pointing `Runs` and `RepositoryPipelines`), and retry-with-detach logic in both `HookRunner.GetOrCreateRepositoryIdAsync` and `RunnerService.GetOrCreateRepository` to handle concurrent inserts.
- **Schtasks runtime (Windows):** Name-based ownership (`Railway\<repoSlug>\<pipelineSlug>.<hex>`) with stable path-based repo slug; implementation fails loudly if `/TR` exceeds 262 chars (MaxTaskRunLength=262) and wraps execution in `cmd /c` with redirection. These behaviors are unit-tested; no Windows host was available for end-to-end validation — treat `schtasks` integration on Windows as "Windows-unverified runtime" until tested on a real Windows machine.
- **Watcher readability** (4d/e): ✅ **Partly addressed** — Extracted shared renderer `HookRunResultRenderer` (used by both `HooksWatchHandler` and `HooksRunHandler`); split `HookWatcher` into orchestration + `HookWatchGlobMatcher` (glob matching/translation) and extracted `FileSystemWatcherWatchEventSource` seam. Some duplication between handlers is eliminated; further splitting of `HookWatcher` could still be done if desired, but the main concerns are addressed.

## Risks / gotchas to respect
- `RunStatus.Succeeded = 0` — set every status explicitly, including `Skipped`.
- `EnsureCreated` is a no-op on existing DBs; the ALTER TABLE shim is the only upgrade path. The DB is global app-data shared by all repos.
- `RunHandler.cs:71` maps "not Succeeded" to a failure — check `Skipped` explicitly.
- New `Pipeline` properties must be EF-ignored and must be added to `PipelineHelper`'s write models, or `convert` silently drops them.
- The CLI now wires `CancellationTokenSource` + `Console.CancelKeyPress` → `app.RunAsync(args, token)` (hooks watch + token plumbing for `hooks run`); exit codes of existing commands are preserved.
- Detached/scheduled processes are separate OS processes writing one SQLite file — busy-timeout (A6).
- Never write to stdout from anything that could run inside the MCP host; the hooks host is the CLI, so this only matters for shared Engine code (`Mcp/` is untouched in v1).
- Stale locks after a crash must expire, or a pipeline can be blocked forever.
- Windows: git hooks run through `sh` (Git for Windows) while the scheduler half is `schtasks` + PowerShell; keep the two paths independent.

## Verification (per AGENTS.md)
```bash
dotnet build Railway.slnx
dotnet test
```
Manual spot-check in a scratch repo:
```bash
railway hooks install                 # writes .git/hooks/post-commit + .railway/hooks/*
git commit -m "trigger"               # commit returns immediately
railway hooks status                  # installed, not drifted, last run hook:post-commit
railway dashboard                     # recent run shows the trigger
railway hooks uninstall               # hooks + gitignore block removed, backups restored
```

## Out of scope
- MCP tools (no `Mcp/` changes; `EngineExceptionMapper` untouched) and any remote/network triggers.
- `pre-commit` gating (blocking, abort-on-failure), `pre-push` abort-on-failure, queueing overlapping runs.
- CI-provider integration (GitHub Actions etc. remain a `convert` target only) and cloud/remote runners.
- EF migrations framework, retry/backoff, log rotation/retention policy, `railway init` prompting for hooks.
- Rich notifier dependencies (`terminal-notifier`, BurntToast) and non-macOS notification polish in P1.
