# Handoff: Get test suite green on xUnit.v3 + FakeItEasy

## Goal

`dotnet build Railway.slnx` compiles, and `dotnet test` is 100% green using
**xUnit.v3** + **FakeItEasy** (Moq/Castle.Core removed). No code has been written
in the repo yet; every fix below was validated first in a throwaway copy of the
repo (see "Evidence").

## Current state (verified, not assumed)

- `dotnet test` today fails **at compile time**:
  `Tests/Integration/IntegrationTestCollection.cs(19,9): error CS0103: The name 'TestServiceRegistrar' does not exist` (missing `using Tests.Infrastructure;`). Nothing runs.
- After that one-line fix alone: **37 tests → 20 fail / 17 pass**.
- Test project references **both** Moq 4.20.72 + Castle.Core 5.2.1 **and**
  FakeItEasy 8.0.0 — none of them is actually used by any test today.
- xUnit in use is v2 (`xunit` 2.9.2); docs (`docs/tasks/mvp-local-pipelines.md`
  → Decisions) already specify **xUnit + FakeItEasy**.

### Root causes (each reproduced individually)

| # | Failure mode | Root cause | Where fixed |
|---|---|---|---|
| C1 | Build error CS0103 | Missing `using Tests.Infrastructure;` in `Tests/Integration/IntegrationTestCollection.cs` | tests |
| C2 | **Every** `CommandApp.RunAsync` returns `-1` (all integration tests) | Hand-rolled `Tests/Infrastructure/TestTypeRegistrar.cs` resolver calls `Activator.CreateInstance` on `IEnumerable<IHelpProvider>` → `MissingMethodException` ("Could not resolve type 'IEnumerable\`1[IHelpProvider]'", CLI 0.55) | tests |
| C3 | Output assertions fail (`console.Output` empty/irrelevant) | Handlers + commands write to **static** `AnsiConsole`; only `InitCommandTests` assigns `AnsiConsole.Console = console` | tests |
| C4 | Dashboard tests show `USAGE: testhost.dll…` or empty | Tests register default command via `AddCommand<DashboardCommand>("")`; must mirror prod: `new CommandApp<DashboardCommand>(registrar)` | tests |
| C5 | `System.IO.FileNotFoundException` at `Interop.Sys.GetCwd()` in ctors | Integration test classes run **in parallel**, each mutates process-wide `Directory.SetCurrentDirectory` and deletes its temp dir → other classes' `GetCurrentDirectory()` explodes | tests |
| C6 | `ArgumentException: An item with the same key has already been added. Key: Cli.Infrastructure.RailwayDbContext` | `LifetimeService` is static + `Add` throws on duplicate registration; fixture + per-test-class registration collide across the assembly | prod (1 line) or tests |
| C7 | `Run_SimplePipeline_Succeeds`: output `Hello`, expected `Hello World` | `RunnerService.ExecuteStep` builds `sh -c "<command>"` with manually embedded quotes → nested quoting eats the inner quotes | **production** |
| C8 | `Validate_InvalidPipeline_ShowsErrors`: expected exit 1, actual 0 | `ValidationHandler` prints invalid output and returns normally → `ValidateCommand` returns 0. Spec (docs, `validate` section): "Exit 0 valid, **1 invalid** / not a yaml / missing path" | **production** |
| C9 | `Init_NotInGitRepo_ReturnsFatalError`: expected exit 1, actual 0 | `InitHandler` prints `fatal: not a git repository …` and returns normally → `InitCommand` returns 0. Spec (docs, `init` section): "Fatal if not in a git repo …, **exit 1**" | **production** |
| C10 | `Discover_NestedDirectories_FindsAllPipelines`: got `ci/nested.yaml`, expected `nested.yaml` | `PipelineDiscoveryService` sets `FileName = Path.GetRelativePath(pipelinesPath, file)` (includes subdirs). Spec (docs): display/select by **file name** | **production** (or test) |

### Migration facts (verified against this machine)

- Offline cache has `xunit.v3` 1.0.0 / `FakeItEasy` 8.0.0 only; **network works**
  (nuget.org reachable), latest stable: `xunit.v3` **4.0.2**, `FakeItEasy`
  **9.0.1**, `xunit.runner.visualstudio` **4.0.1**, `Microsoft.NET.Test.Sdk` **18.11.0**.
- ✅ Verified working: `xunit.v3` **3.2.2** + `xunit.runner.visualstudio` 3.1.5 +
  `Microsoft.NET.Test.Sdk` 18.11.0 + `FakeItEasy` 9.0.1, `<OutputType>Exe</OutputType>`,
  VSTest mode, plain `dotnet test`, SDK 10.0.401. All test sources compile
  unchanged (`using Xunit;`, `[Fact]`, `[CollectionDefinition]`, `IDisposable`,
  all `Assert.*` used here are v3-compatible). FakeItEasy fake verified with a
  real `A.Fake<IValidateService>` test.
- ⚠️ `xunit.v3` **4.0.2** (latest) transitively pulls
  `Microsoft.Testing.Platform.MSBuild` **2.5.1**, which **hard-errors under VSTest
  mode on .NET 10 SDK**: *"Testing with VSTest target is no longer supported …
  opt-in to the new dotnet test experience."* Using it requires a `global.json`
  with `"test": { "runner": "Microsoft.Testing.Platform" }` and changes
  `dotnet test` semantics (`--project`/`--solution`, trait-based filters).
  **Recommendation: pin `xunit.v3` 3.2.2 and keep the current `dotnet test`
  workflow** (matches AGENTS.md commands). Revisit on next major bump.
- `xunit.runner.visualstudio` 3.x supports both xUnit v2 and v3 projects.
- Repo has no `global.json`, `NuGet.config`, or `Directory.Build.props`.

### Evidence (what was already proven in a sandboxed copy)

Applying only test-side fixes C1–C6 (+ serializing the integration collection):
**37/40 tests pass** (3 probe tests included). The 3 remaining failures are
exactly C8, C9, C10. With C7 applied, all 6 `run` tests pass. I.e. the fix list
below is exhaustive; no unknown failures were observed.

---

## Open decisions (confirm at approval)

1. **Production changes in scope?** Recommended: **yes** for C7–C10 (+C6 overwrite).
   `docs/tasks/mvp-local-pipelines.md` is the behavioral spec and the tests encode
   it; changing tests instead would contradict the documented exit-code and
   display rules. *(User was asked; answer pending a final pick.)*
2. **FakeItEasy depth.** Two options:
   - **(a) Minimal** — remove Moq/Castle.Core, keep FakeItEasy referenced, add
     fakes where new unit tests need them. Low risk.
   - **(b) WS6-aligned (recommended by docs)** — additionally rewrite/extend unit
     tests so they exercise **real production code** with `A.Fake<…>()` (e.g.
     `RunHandler` with faked `IRunnerService`/`IPipelineDiscoveryService`/
     `IValidateService`; `ValidationHandler` with faked `IValidateService`),
     replacing `RunnerServiceTests`'s copied-logic helpers that currently test
     re-implemented code instead of `RunnerService` itself.
3. **xUnit.v3 version/mode.** Recommended: **3.2.2 + VSTest** (verified, zero
   workflow change). Alternative: 4.0.2 + MTP mode (adds `global.json`, changes
   `dotnet test` flags).

---

## Plan

### WS-A — Test infrastructure (unblocks everything)

- [x] **A1.** `Tests/Integration/IntegrationTestCollection.cs`: add
  `using Tests.Infrastructure;` (C1).
- [x] **A2.** Replace `Tests/Infrastructure/TestTypeRegistrar.cs` internals with a
  **Microsoft.Extensions.DependencyInjection-backed** `ITypeRegistrar`/`ITypeResolver`
  (`IServiceCollection` → `BuildServiceProvider()`; `Resolve(type)` =
  `GetService(type)`), keeping the existing ctor shape
  `(TestConsole? console = null, string? databasePath = null)` so test call sites
  don't churn (C2). Register in the container: `TestConsole` +
  `IAnsiConsole` instance, all handler/service interfaces, `RailwayDbContext`
  factory, **and the command types themselves** (`InitCommand`, `ValidateCommand`,
  `ListCommand`, `RunCommand`, `DashboardCommand`) so Spectre can construct them.
  Package `Microsoft.Extensions.DependencyInjection` is already referenced.
  Rationale: MS DI resolves `IEnumerable<T>` natively, which fixes
  `--help`/`RunAsync` returning `-1`.
- [x] **A3.** Capture console output everywhere: after each `new TestConsole()`
  in `ValidateCommandTests`, `ListCommandTests`, `RunCommandTests`,
  `DashboardCommandTests`, assign `AnsiConsole.Console = console;` (C3).
  Prefer a tiny shared helper in `Tests/Infrastructure` (e.g.
  `TestConsoleFactory.Create()`) to avoid repeating this in ~15 tests; also add
  `using Spectre.Console;` where missing.
- [x] **A4.** Dashboard tests: switch to `new CommandApp<DashboardCommand>(registrar)`
  and delete `c.AddCommand<DashboardCommand>("")` — mirroring `Cli/Program.cs`
  (C4). Applies to all 6 `DashboardCommandTests` app setups (incl. the
  run-then-dashboard ones).
- [x] **A5.** Serialize integration tests: add
  `[Collection("Integration Tests")]` to `ValidateCommandTests`, `ListCommandTests`,
  `RunCommandTests`, `DashboardCommandTests` (fixture + `DisableParallelization = true`
  already exist in `IntegrationTestCollection.cs`). Verified: removes the
  `GetCwd`/`FileNotFoundException` races while unit tests remain parallel (C5).
  No assembly-wide `[CollectionBehavior]` needed.
- [x] **A6.** Consolidate `LifetimeService` registration into the collection
  fixture: remove the per-class `TestServiceRegistrar.RegisterServices(...)` calls
  from test constructors (incl. `DashboardCommandTests._databasePath`), so the
  fixture registers exactly once per collection (pairs with B4 for safety, C6).
- [x] **A7.** Cleanup (optional): drop the `// Debug` `Console.WriteLine`s in
  `ListCommandTests.SetRepoDirectory` and `InitCommandTests`.

### WS-B — Production fixes (tests + docs are the spec)

- [x] **B1.** `Cli/Services/RunnerService.cs` → `ExecuteStep`: stop embedding the
  command in `Arguments = "-c \"…\""`. Add `-c` (or `/c` on Windows) and the raw
  command via `ProcessStartInfo.ArgumentList` as separate argv entries (C7).
  Verified: `echo "Hello World"` then emits `Hello World` and all `run` tests pass.
- [x] **B2.** `validate` exit code (C8): make invalid pipelines fail.
  `Cli/Handlers/ValidationHandler.cs` / `IValidationHandler` should signal
  invalidity (return `Task<bool>` or throw a dedicated
  `PipelineValidationException`), and `Cli/Commands/ValidateCommand.cs` return
  `1` while keeping the existing printed output (`Pipeline is invalid`, error
  list). Non-yaml / missing-path paths already return 1. Keep `--verbose`/`0` for
  valid as-is.
- [x] **B3.** `init` exit code (C9): `Cli/Handlers/InitHandler.cs` must signal
  the not-a-git-repo case (bool return or `NotAGitRepositoryException`);
  `Cli/Commands/InitCommand.cs` returns `1` after the message is printed.
  Keep idempotent re-init returning 0.
- [x] **B4.** `Cli/Services/LifetimeService.cs`: change the three `_services.Add(...)`
  calls to indexer assignment (`_services[key] = …`) so re-registration
  overwrites instead of throwing (C6). `Program.cs` registers each key once, so
  no prod behavior change. *(Alternative, if prod changes are rejected: a
  process-wide "register once" guard in `TestServiceRegistrar` with a shared temp
  DB — then A6 alone suffices.)*
- [x] **B5.** Nested pipeline identity (C10): in
  `Cli/Services/PipelineDiscoveryService.cs` set `FileName =
  Path.GetFileName(file)` and keep `Path` = full path (already populated).
  Sanity-check downstream: `RunHandler` already matches on
  `Path.GetFileName(p.FileName)` and lists duplicate candidates via `p.Path`, so
  resolution + duplicate detection keep working; dashboard/list show
  `<file-name> - name: …` as specced. *(If prod changes are rejected: update the
  single assertion in `PipelineDiscoveryServiceTests.Discover_NestedDirectories_FindsAllPipelines`
  to expect `ci/nested.yaml` instead.)*

### WS-C — Framework migration (xUnit.v3 + FakeItEasy)

- [x] **C1.** `Tests/Tests.csproj` package surgery:
  - remove `xunit` 2.9.2, `Moq` 4.20.72, `Castle.Core` 5.2.1;
  - add `<PackageReference Include="xunit.v3" Version="3.2.2" />`;
  - bump `xunit.runner.visualstudio` 3.0.1 → 3.1.5;
  - bump `Microsoft.NET.Test.Sdk` 18.7.0-preview… → 18.11.0 (also kills NU1603);
  - bump `FakeItEasy` 8.0.0 → 9.0.1;
  - add `<OutputType>Exe</OutputType>` (xUnit.v3 test projects are executables).
- [x] **C2.** No test-source changes expected for v3 (all APIs used are
  compatible — verified). If the build flags anything (e.g. analyzer rules), fix
  in test code only.
- [x] **C3.** Run `dotnet restore` + `dotnet test`; confirm the VSTest path still
  works (no `global.json`/MTP migration).
- [x] **C4.** Verify Moq/Castle fully gone: `grep -r "Moq\|Castle" Tests` → no hits.

### WS-D — FakeItEasy adoption (scope per open decision 2)

- [x] **D1 (decision 2a, always).** Keep FakeItEasy as *the* mocking library; any
  new test needing a mock uses `A.Fake<T>()` / `A.CallTo(...)`, never Moq.
- [x] **D2 (decision 2b, if approved).** Add unit tests exercising real production
  code with fakes, per docs WS6:
  - `RunHandler` (faked `IRunnerService`, `IPipelineDiscoveryService`,
    `IValidateService`): not-found → prints `Pipeline file not found` + throws;
    ambiguous names; invalid pipeline → `Pipeline is invalid`; success/failure
    messaging.
  - `ValidationHandler` with faked `IValidateService`: valid vs invalid rendering,
    non-yaml/missing path errors.
  - `DashboardHandler` with faked `IDashboardService`: empty vs populated views,
    valid/invalid pipeline markup, run status markup.
  - Replace `RunnerServiceTests`'s copied private-logic helpers with tests against
    the real `RunnerService` where feasible (topological sort / env merge are
    currently duplicated in the test file, not testing prod code).

### Verification (definition of done)

1. `dotnet build Railway.slnx` → 0 errors, 0 NU1603.
2. `dotnet test` → **0 failed** (≥ 37 tests; more if D2 lands).
3. Run `dotnet test` **twice in a row** → no order/parallelism flakiness
   (guards C5/C6 regressions).
4. Per AGENTS.md: C# changes → tests run for the affected project before
   reporting completion. Do not run the app.
5. Manual smoke (user-run, optional): `init` → `validate` on an invalid pipeline
   exits `1` (B2), `init` outside a git repo exits `1` (B3), `run` with quoted
   echo prints the full string (B1).

## Out of scope

- `DatabasePathHelper` macOS/Linux path swap (explicitly deferred in docs).
- Narrowing `PipelineHelper.TryParse`'s broad `catch`, XML-doc conventions, and
  other WS0-review leftovers.
- MTP/`dotnet test` migration (only if decision 3 flips to xunit.v3 4.x).

---

## Review findings (WS-A + WS-B implemented)

Status: `dotnet build Railway.slnx` → 0 errors (2 × NU1603 pre-existing warnings from
the unpinned `Microsoft.NET.Test.Sdk` preview, WS-C). `dotnet test` → **37/37 passing**,
repeated 3× with no order/parallelism flakiness.

Deviations from the plan:

- **A1 was already present** in the working tree when this workstream started
  (`using Tests.Infrastructure;` in `IntegrationTestCollection.cs`); `InitCommandTests`
  had also already been partly migrated (collection attribute + fixture ctor + `CreateApp`
  helper). No further change was needed for A1.
- **A2:** implement `Resolve(type)` as `provider.GetService(type)` with a defensive
  `catch (InvalidOperationException) → null`. Rationale (verified by decompiling
  `Spectre.Console.Cli` 0.55.0): Spectre wraps our resolver in
  `TypeResolverAdapter`, which converts any thrown exception into
  `CommandRuntimeException.CouldNotResolveType` → hard `-1` exit code, but falls back to
  `Activator.CreateInstance` when `Resolve` returns `null`. Returning `null` on failure is
  therefore strictly safer than throwing. `IEnumerable<IHelpProvider>` (the C2 root cause)
  now resolves to an empty enumerable and Spectre falls back to `new HelpProvider(...)`.
- **A2:** `Build()` returns a resolver over a **fresh** `ServiceProvider` per run, because
  Spectre calls `Register`/`RegisterInstance`/`RegisterLazy` (command + settings types,
  `IConfiguration`, `CommandModel`, `IAnsiConsole`) *after* the registrar is constructed and
  *before* `Build()`; a provider built in the ctor would miss them.
- **A3:** added `Tests/Infrastructure/TestConsoleFactory.Create()`, which creates a
  `TestConsole` and assigns `AnsiConsole.Console`; used in `Validate/List/Run/Dashboard`
  integration tests (~18 call sites). `InitCommandTests` keeps its existing `CreateApp`
  assignment.
- Cleanup after review: dropped the unused non-interface `Resolve<T>()` member from the
  rewritten resolver (it is not part of `ITypeResolver` and was never called), and added
  `<inheritdoc/>` to the `ITypeRegistrar`/`ITypeResolver` implementations.

Identified smells / suggested follow-ups (deliberately **not** changed — out of scope):

| # | Smell | Where | Suggested fix |
|---|---|---|---|
| R1 | Duplicated Code / Shotgun Surgery | The service map now exists 3×: `Cli/Program.cs`, `Tests/Infrastructure/TestServiceRegistrar.cs`, `Tests/Infrastructure/TestTypeRegistrar.cs` | Extract one shared registration list (or have the test registrar reuse `TestServiceRegistrar`'s mapping) so adding a service touches one place |
| R2 | Global mutable state (pre-existing) | `AnsiConsole.Console` assignment in `TestConsoleFactory` / `InitCommandTests` | Not fixable without changing prod handlers to take `IAnsiConsole`; acceptable for now |
| R3 | Resource leak (pre-existing) | `TestTypeRegistrar` creates a per-registrar temp SQLite dir that is never deleted; `ServiceProvider`s are never disposed | Dispose provider in `Build()`'s resolver (`ITypeResolver` is not `IDisposable` in 0.55, so optional) and/or share the fixture DB path |
| R4 | Over-broad catch | `TestTypeResolver.Resolve` swallows `InvalidOperationException` | Intentional (R2/deviation note above); if DI misconfiguration should fail loudly, log or rethrow during local debugging |
| R5 | XML-doc conventions | All `Cli/Handlers/Interfaces/*` (incl. the now-`Task<bool>` `Handle` members) lack XML docs | Explicitly listed as out of scope in this doc ("Out of scope" §) |

Behavioral verification of B1–B5 (matches spec in `docs/tasks/mvp-local-pipelines.md`):

- B1: `run: echo "Hello World"` now emits the full string (was `Hello`).
- B2: `validate` on an invalid pipeline prints `Pipeline is invalid` + error list and exits `1`;
  valid stays `0`; non-yaml/missing path still exits `1`.
- B3: `init` outside a git repo prints `fatal: not a git repository ...` and exits `1`;
  first init and idempotent re-init stay `0`.
- B4: re-registering a `LifetimeService` key overwrites instead of throwing; `Program.cs`
  registers each key once, so no prod behavior change.
- B5: nested pipeline discovered as `nested.yaml` (`Path` keeps the full path); `RunHandler`
  matching on `Path.GetFileName(p.FileName)` and duplicate reporting via `p.Path` still work.

---

## Review findings (WS-C implemented)

Status: `dotnet build Railway.slnx` → **0 errors, 0 warnings** (NU1603 gone).
`dotnet test` → **37/37 passing**, run twice in a row with no order/parallelism flakiness.
`grep -rni "moq\|castle" Tests Cli` (src + csproj, bin/obj excluded) → no hits.
No `global.json` added; `dotnet test` still uses the VSTest path (output:
`Test run for … Tests.dll (.NETCoreApp,Version=v10.0)`).

Deviation from the plan:

- **C2 required test-source edits after all.** `xunit.v3` 3.2.2 transitively enables
  the `xunit.analyzers` **xUnit1051** rule ("calls to methods which accept
  CancellationToken should use `TestContext.Current.CancellationToken`"), which the
  v2 baseline did not raise. It flagged **34 call sites** across 5 files
  (`CommandApp.RunAsync`, `File.WriteAllTextAsync`, `File.ReadAllTextAsync`). Per
  C2's "fix in test code only", each call now passes
  `TestContext.Current.CancellationToken`. No behavioral change (the token was
  otherwise `CancellationToken.None`); specifically:
  - `Tests/Unit/PipelineDiscoveryServiceTests.cs` — 10 × `File.WriteAllTextAsync`.
  - `Tests/Integration/{Init,Validate,List,Run,Dashboard}CommandTests.cs` —
    23 × `CommandApp.RunAsync` + 1 × `File.ReadAllTextAsync`.
  - `App.RunAsync(IEnumerable<string>, CancellationToken)` verified against
    `Spectre.Console.Cli` 0.55.0 XML docs.

Identified smells / suggested follow-ups (not changed — out of scope):

| # | Smell | Where | Suggested fix |
|---|---|---|---|
| R6 | Long lines (up to 140 chars) after the C2 token fix | The 34 edited call sites | Extract a per-test `var ct = TestContext.Current.CancellationToken;` if a line-length convention is ever adopted (repo has no `.editorconfig`) |
| R7 | Repeated fixture boilerplate | All `*CommandTests` ctors | Covered by R1 above (shared registration); the token edits did not change this shape |

---

## Review findings (WS-D implemented)

Status: `dotnet build Railway.slnx` → 0 errors, 0 warnings.
`dotnet test` → **52/52 passing**, run twice in a row with no order/parallelism
flakiness. **15 new tests** (37 → 52); 1 planned test was merged into another to
remove duplication (see below). `grep -rni "moq\|castle" Tests Cli` → no hits;
every new fake uses `A.Fake<T>()` / `A.CallTo(...)`.

### D2a choice (documented)

Made `RunnerService.TopologicalSort`, `BuildEnvironment`, and `MergeEnvironments`
`internal static` (visibility only — no body change) and added
`Cli/Properties/AssemblyInfo.cs` with `[assembly: InternalsVisibleTo("Tests")]`.
This is the recommended minimal option: `RunnerServiceTests` now calls the real
helpers and the eight copy-pasted `RunnerService*` helper re-implementations were
deleted entirely. The alternative (driving everything through public
`RunnerService.Run`) was also done for the two new end-to-end tests, but is not
used for the pure-helper cases where direct calls give exact, fast coverage.

### Files added / changed

Added:

- `Cli/Properties/AssemblyInfo.cs` — `InternalsVisibleTo("Tests")`.
- `Tests/Infrastructure/TempDirectory.cs` — reusable disposable temp directory.
- `Tests/Unit/RunHandlerTests.cs` — 6 tests of real `RunHandler` (faked
  `IRunnerService` / `IPipelineDiscoveryService` / `IValidateService`): not
  found, ambiguous candidates, unparseable yaml, invalid pipeline (also asserts
  the runner is never invoked), success message, failure exit-code message.
- `Tests/Unit/ValidationHandlerTests.cs` — 4 tests of real `ValidationHandler`
  (faked `IValidateService`): non-yaml `ArgumentException`, missing-file
  `ArgumentException`, valid renders/`true`, invalid renders/`false`.
- `Tests/Unit/DashboardHandlerTests.cs` — 3 tests of real `DashboardHandler`
  (faked `IDashboardService`): empty view placeholders, valid/invalid pipeline
  markup, run-status markup (Succeeded/Failed/Running/Cancelled).
- `Tests/Unit/RunnerServiceRunTests.cs` — 2 end-to-end tests of real
  `RunnerService.Run` against a real SQLite `TestDatabaseFixture`: a `sh -c
  echo` step asserting persisted `Run`/`RunJob`/`RunStep` state, and a circular
  dependency asserting `InvalidOperationException`.

Changed:

- `Cli/Services/RunnerService.cs` — three pure helpers `private static` →
  `internal static` (no body change; no public-surface change).
- `Tests/Unit/RunnerServiceTests.cs` — now exercises the real production
  helpers; all copied-logic helpers removed.
- `docs/tasks/test-suite-green-xunitv3-fakeiteasy.md` — this section.

Notable fakes: `A.Fake<IRunnerService>()`, `A.Fake<IPipelineDiscoveryService>()`,
`A.Fake<IValidateService>()`, `A.Fake<IDashboardService>()`, all configured via
`A.CallTo(...)`; `MustNotHaveHappened()` / `MustHaveHappenedOnceExactly()` used
for interaction assertions.

### Serialization of the new tests

`RunHandler` resolves the repo from the current working directory
(`RepositoryHelper`) and all three handlers write to the static
`AnsiConsole.Console`; `RunnerService.Run` also spawns processes and writes to
that console. The new test classes therefore carry
`[Collection("Integration Tests")]`, reusing the existing
`DisableParallelization = true` collection so they never race the integration
suite or each other over cwd / `AnsiConsole.Console`. `RunnerServiceTests`
(pure helpers only) stays parallel — it touches neither. No prod change was
needed to bypass the filesystem: `TestRailwayRepository` provides the temp
git/railway repo.

### Deliberately left out / deviations

- **No prod behavior change.** The only prod edit is method visibility (D2a).
- **One test merged, not added:** a standalone "runner is skipped when invalid"
  test was folded into `Handle_InvalidPipeline_PrintsValidationErrorsAndDoesNotRun`
  (same setup plus a `MustNotHaveHappened` assertion) to avoid duplicated setup.
- No `internals` were made public and no test asserts copied logic anymore.

### Identified smells / suggested follow-ups (not changed)

| # | Smell | Where | Suggested fix |
|---|---|---|---|
| R8 | Duplicated temp-file setup | `ValidationHandlerTests` valid/invalid tests | Extracted `TempDirectory`; the remaining `File.WriteAllText` line is test-specific |
| R9 | Static `AnsiConsole.Console` coupling forces handler tests into the serialized collection | new `*HandlerTests` | Pre-existing R2; would need prod handlers to accept `IAnsiConsole` |
| R10 | `RunnerService.Run` persists a `Running` `Run` row before `TopologicalSort` throws on a circular dependency | `RunnerService.Run` | Prod behavior; out of scope (circular config is rejected by `validate` before `run`) |


