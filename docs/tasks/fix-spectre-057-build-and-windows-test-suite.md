# Fix: Spectre 0.57 build break + green Windows/Linux test suite

Status: **diagnosed, not implemented**. Written 2026-10-18 from a Windows machine investigation.
Related (partially overlapping, also unimplemented): `docs/tasks/fix-windows-db-and-hook-issues.md` — its **Fix 2** (installer backup restore) is the same bug as step 2 below; implement once.

## Context

Package updates landed in two waves:

- Committed: Spectre.Console(.Cli/.Testing) 0.55.0 → 0.57.2 (`34fd558`), EF Core Sqlite / MS.DI / Hosting 10.0.12 → 10.0.13, ModelContextProtocol 2.1.0 → 2.2.0, xunit.runner.visualstudio 3.1.5 → 4.0.1.
- Uncommitted (working tree): the remaining 10.0.12 → 10.0.13 csproj bumps and an untracked `global.json` selecting the `Microsoft.Testing.Platform` runner for `dotnet test`.

Three symptoms: (1) solution no longer builds, (2) many tests fail on Windows, (3) `HookInstallerTests.Sync_WhenStaleHookHadBackup_RestoresTheBackup` fails on Linux.

## Verified findings

### 1. Build break — Spectre 0.57 made `AsyncCommand.ExecuteAsync` public

Spectre.Console.Cli 0.57.x changed `AsyncCommand.ExecuteAsync(CommandContext, CancellationToken)` and `AsyncCommand<TSettings>.ExecuteAsync(...)` from `protected abstract` to `public abstract`. All 12 commands in `Cli/Commands/*.cs` still declare `protected override` → 12 × CS0507.

Verified: flipping all 12 to `public override` builds clean (0 warnings, 0 errors); no other Spectre API breakage in Cli/Tests.

### 2. Windows test failures — 5 direct-run buckets + nondeterminism

Direct runner baseline (`dotnet Tests\bin\Debug\net10.0\Tests.dll`): **546 total, 54 failed, 12 skipped**. A `dotnet test --solution Railway.slnx --verbosity diagnostic` run differed (651 total / 159 failed) and one earlier `dotnet test` run reported "Zero tests ran, error: 1" — the suite is **nondeterministic** (see bucket F).

| Bucket | Count | Root cause (verified) |
| --- | --- | --- |
| A. `IOException: The process cannot access the file 'data.db' ...` thrown from fixture `Dispose` | 36 | Microsoft.Data.Sqlite **connection pooling** (default since v9) keeps the physical connection (and the db file handle) open after `ServiceProvider`/`DbContext` disposal. `Directory.Delete` of the temp DB dir then fails **on Windows only**. Reproduced in isolation: `EngineInitializerTests` fails 7/8 alone, provider is properly disposed (`using var`), so it is pooling, not a leak. Crash sites: `Tests/Infrastructure/TestFixtures.cs:30`, `TempDirectory.cs:28`, `Tests/Integration/IntegrationTestCollection.cs:24`. |
| B. `Assert.Contains` sub-string not found on hook scripts / crontab | 4 | `HookScriptHelper.QuoteSh` (`Engine/Helpers/HookScriptHelper.cs:117`) correctly escapes `\` → `\\` for POSIX sh. Tests assert **raw** Windows paths instead of `QuoteSh(...)`: `Tests/Unit/HookInstallerTests.cs:45,46,57,184,185`; `Tests/Unit/SchedulerTests.cs:257` (crontab expected line); `Tests/Integration/HooksGitLifecycleTests.cs:335` (`CliDllPath`). |
| C. `RunHistoryServiceTests.GetRunAsync_TruncatesStepOutputToBoundedLength` | 1 | Pipeline step runs POSIX `head -c 6000 /dev/zero \| tr '\0' 'a'` (`Tests/Integration/RunHistoryServiceTests.cs:97`); step exits 1 on Windows. |
| D. `HooksScheduleTests.Install_MaterializesScheduleInFakeStore_AndStatusReportsItInstalled` | 1 | `FakeScheduler.Write` (`Engine/Services/Schedulers/FakeScheduler.cs:183`) uses `JsonSerializer.Serialize` with `WriteIndented`; the indented newline is env-dependent on this runtime → `\r\n` content on Windows vs the `\n` expectation (`Tests/Integration/HooksScheduleTests.cs:126`). |
| E. `HookInstallerTests.Sync_WhenStaleHookHadBackup_RestoresTheBackup` | 1 | Same as Linux (bucket below). Fails on **both** OSes. |
| F. Run-to-run variance (54 vs 159 failures; one run: 0 tests ran) | — | Many unit test classes mutate the **process-global cwd** (`TempGitRepository`/`TestRailwayRepository` in `Tests/Infrastructure/TestFixtures.cs`) while xunit runs collections in parallel → cwd races + collection-cleanup failure cascades. AGENTS.md only serializes the "Integration Tests" collection. |

### 3. Linux (and Windows) — `RemoveStaleGitArtifacts` lazy-enumeration race

`Engine/Orchestration/HookInstaller.cs:501-520`: `Directory.EnumerateFiles(hooksDirectory)` is enumerated **lazily while the loop mutates the directory**. When the stale `post-commit` entry is processed first, the loop deletes it and restores the backup via `File.Move(backupPath, hookPath, overwrite: true)`; the still-open enumerator later yields the now-gone `post-commit.railway-backup` entry and `File.ReadAllText(hookPath)` (line 505) throws `FileNotFoundException`. Entry order is filesystem-dependent (Linux `readdir` hash order hit it in CI; Windows NTFS also hit it here). The wrapper scan (lines 536-547) and `RemoveWrapperArtifacts` (567-580) have the same lazy pattern.

## Steps (user-approved)

1. **Fix build**: change `protected override` → `public override` on `ExecuteAsync` in the 12 `Cli/Commands/*.cs` files (Convert, Dashboard, HooksInstall, HooksRun, HooksStatus, HooksSync, HooksUninstall, HooksWatch, Init, List, Run, Validate). Build `Railway.slnx` to confirm.
2. **Fix hook enumeration race** (covers bucket E + Linux + the pre-existing doc's Fix 2): in `RemoveStaleGitArtifacts`, materialize the listing (`.ToList()`), skip entries ending with `HookScriptHelper.BackupSuffix` in the stale scan (backups are restored with their base hook, never evaluated alone — satisfies `Sync_WithOrphanBackupAndNoMatchingHook_LeavesItAndDoesNotThrow`), and guard reads with `File.Exists`. Apply the same defensive guard to the wrappers loop and `RemoveWrapperArtifacts`.
3. **Fix SQLite lock cascades (bucket A)**: call `SqliteConnection.ClearAllPools()` before directory deletion in `TestDatabaseFixture.Dispose`, `IntegrationTestFixture.Dispose`, and `TempDirectory.Dispose` — one small shared helper in `Tests/Infrastructure` (delete-with-clear-and-retry).
4. **Fix Windows path assertions (bucket B)**: build the expectations with `HookScriptHelper.QuoteSh(...)` at `HookInstallerTests.cs:45,46,57,184,185`, `SchedulerTests.cs:257` (both wrapper and log paths), `HooksGitLifecycleTests.cs:335`. Then grep the test project for any remaining raw-path-into-script assertions.
5. **Fix POSIX-only truncation test (bucket C)**: dynamic-skip on Windows (existing precedent: `$XunitDynamicSkip$uses POSIX sh background pid capture` in `SpawnedProcessTrackerTests` / `RunnerServiceCancellationTests`).
6. **Pin JSON newline (bucket D)**: set explicit `JsonWriterOptions { Indented = true, NewLine = "\n" }` in `FakeScheduler` (deterministic on all OSes; do not make the test newline-agnostic).
7. **Tame flakiness (bucket F)**: disable parallelization assembly-wide — `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in the Tests project (user chose this over serializing only cwd-mutating classes). Slower suite, deterministic order on both OSes.
8. **Verify** (see below).

Note: no CI workflow files exist in this checkout (`.github/workflows/` absent here); the Linux failure was reproduced on GitHub Actions by the user. Linux verification therefore happens on the user's side/CI after the fixes land.

## Acceptance criteria

- `dotnet build Railway.slnx` succeeds.
- `dotnet Tests\bin\Debug\net10.0\Tests.dll` (or `dotnet test --solution Railway.slnx`, note `--solution` is required under the MTP runner): **0 failed** on Windows, and stable across two consecutive runs (no bucket-F variance, no "Zero tests ran").
- `Sync_WhenStaleHookHadBackup_RestoresTheBackup` passes on Windows and Linux.
- No test asserts an unescaped Windows path inside a POSIX script; no fixture `Dispose` throws `IOException` on `data.db`.

## Verification steps

1. `dotnet build Railway.slnx`.
2. Run the suite twice via `dotnet Tests\bin\Debug\net10.0\Tests.dll` → both green, identical totals.
3. Run once via `dotnet test --solution Railway.slnx --nologo` → confirms MTP runner works and the zero-tests flake is gone.
4. User runs the Linux suite/CI → hook test green.
