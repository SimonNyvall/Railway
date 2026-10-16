# Task: `railway convert` — import GitHub Actions / GitLab / Azure DevOps YAML into a Railway pipeline

## Goal
Add a CLI command that takes a path to a GitHub Actions, GitLab CI, or Azure DevOps pipeline YAML
file, converts it **best-effort** into a Railway pipeline file under `.railway/pipelines/`, reports
every construct that could not be mapped as **yellow warnings**, and **validates the written file**
with the existing `IPipelineValidator` at the end.

## Decisions (confirmed with user)
| Topic | Decision |
|---|---|
| Command shape | `railway convert <PATH> [--format github\|gitlab\|azure] [--output <NAME>] [--force]` |
| Format detection | Auto-detect from content + filename; if ambiguous/failed → **interactive selection prompt** (Spectre); `--format` skips it and is **required when non-interactive (no TTY)** → error demanding `--format`, exit 1 |
| Output destination | Write to `.railway/pipelines/<derived-name>.yaml` (derived from source file name; `--output` overrides). **Refuse overwrite** unless `--force` (message + exit 1) |
| Conversion policy | Best-effort: mapped parts written; every unmappable construct printed as **yellow warnings** with source context (e.g. `job 'build': 'runs-on' has no equivalent`). Hard-fail (exit 1) only if source isn't recognizable CI YAML, has no convertible jobs, conversion/serialization fails, or final validation fails |
| Validation at end | Run existing `IPipelineValidator` on the written file. On errors: print red, then **interactive confirm** `Keep the file anyway?` (default **No** → delete file; Yes → keep). **Either way exit 1**. Non-interactive → keep file, print errors, exit 1 (no prompt) |
| Clean run output | `[green]✓[/] Converted + validated` + output path, then yellow warnings section (if any), then the same job tree `validate` shows. Warnings do not affect exit code (0) |
| Surface | **CLI only** — no MCP `convert_pipeline` tool, no changes under `Mcp/` |
| Tests | Unit (detection, mapping, warning collection) + integration (command in temp repo) following existing patterns |

## v1 mapping scope (confirmed)
Map:
- Jobs → Railway jobs (GH: `jobs.<id>`; GitLab: top-level jobs; ADO: `- job:` inside `- stage:`)
- GH `needs` → `depends_on`
- Stage-based sources (GitLab `stages:` order, ADO `- stage:` order) → each job `depends_on` all jobs of the previous stage (heuristic; document on interface)
- `steps[].run` (GH) / `script:` list (GL/ADO, joined per step, one step per list entry **or** one step with `;`-joined lines — pick one, document it) → `Step.Run`
- Step/job names → `Step.Name` / job key
- Workflow/pipeline-level `env:` + `variables:` (GH `env`, GL `variables`, ADO `variables`) → `Pipeline.Variables`; job-level `env` → `Job.Env`
- Timeouts: GH `timeout-minutes`, ADO `timeoutInMinutes` → `Job.Timeout` (TimeSpan)

Everything else → warning, notably: `uses:` (actions), `runs-on`, `pool:`, `matrix`/`strategy`,
`container`, `services`, `with:`/`args`, triggers (`on:`, `trigger:`, `pr:`), `if:`, `conditions:`,
`permissions`, `continue-on-error`, `rules:`/`only:`/`when:`, GL `image:`, YAML anchors/`!reference`,
`defaults`, `environment:`, concurrency.

## Verified facts (from repo)
- Schema: `Engine/Domain/{Pipeline,Job,Step}.cs` — `Pipeline{Name, Jobs:Dictionary<string,Job>, Variables}`, `Job{DependsOn, Env, Timeout, Steps, Locks}`, `Step{Name, Run, Env, Timeout}`. `Jobs`/`Variables` are EF-ignored in `RailwayDbContext.OnModelCreating` (not `[NotMapped]`).
- Example target YAML: `.railway/pipelines/build-and-test.yaml`.
- Parse: `Engine/Helpers/PipelineHelper.TryParse/Parse` (YamlDotNet, `UnderscoredNamingConvention`, `IgnoreUnmatchedProperties`). **No serializer for pipelines yet** — precedent is `RailwayManifestHelper.Write()` (`SerializerBuilder` + underscored convention).
- Validation: `Orchestration/PipelineValidator.ValidateAsync(filePath)` → `ValidationResult(Pipeline, Errors)` (returns, doesn't throw); underlying checks in `Services/ValidateService.IsValid` (≥1 job, deps exist, ≥1 step each, non-empty `run`). Throws `ArgumentException` for missing/non-yaml file.
- Engine layering convention: `Orchestration/Interfaces/I*Query|I*Converter` (explicit paths, no cwd, no Spectre, returns records / throws domain exceptions) → `Services/Interfaces/I*Service`. Registration in `Engine/ServiceCollectionExtensions.AddEngine`.
- Command anatomy: `Cli/Commands/<X>Command.cs` (settings + exception→exit code) → `Cli/Handlers/<X>Handler` (renders with `AnsiConsole.MarkupLine`) → Engine; registered in `Cli/Program.cs` (`AddTransient` + `AddCommand<T>("name").WithDescription(...)`).
- `RunHandler` pattern: handler renders errors then **rethrows** plain BCL exception → command's generic catch returns 1. Reuse for hard failures.
- All commands require `.railway` folder (fatal + hint `railway init`). CLI passes `Directory.GetCurrentDirectory()` into Engine.
- Repo resolution for write target: mirror `RepositoryHelper` usage (`PipelineRunner`/`DashboardService`) to locate repo root + `.railway/pipelines`.
- `RunStatus.Succeeded = 0` gotcha, `PipelineHelper.TryParse` broad catch — both irrelevant here except: use throwing `Parse`/`IPipelineValidator` and map exceptions in the command, don't rely on `TryParse`.

## Assumptions / verify during implementation
- **Serialization of `Pipeline`**: EF props (`Id`, `Path`, `RepositoryPipelines`) must not leak into output; `Step.Timeout` is non-nullable `TimeSpan` and will serialize as `00:00:00`. Solution: `.Ignore<Pipeline>(...)` + `DefaultValuesHandling.OmitDefaults` (or a small write-DTO) — **verify round-trip**: written file must re-parse via `PipelineHelper.Parse` and validate.
- Spectre enum option binding: name enum members `GitHub`, `GitLab`, `Azure` so `--format github|gitlab|azure` binds (confirm case-insensitive parsing in the installed Spectre version).
- TTY detection: `AnsiConsole.Profile.Capabilities.Interactive` (confirm API in installed Spectre version).
- GitLab job→stage dependency heuristic (prev-stage jobs) may over-connect pipelines with single-stage jobs — acceptable for v1; all jobs in first stage get empty `DependsOn`.

---

## Step 1 — Engine: conversion service (pure mapping, no IO beyond reading source)
1. `Engine/Domain/SourceFormat.cs` — enum `GitHub, GitLab, Azure` (names chosen for CLI binding).
2. `Engine/Services/Interfaces/IPipelineConvertService.cs` + `Engine/Services/PipelineConvertService.cs`:
   - `DetectFormat(string sourcePath)` → `SourceFormat?` — heuristics: path (`.github/workflows/`, `.gitlab-ci*`, `azure-pipelines*`) first, then content (`on:` + `runs-on` → GitHub; `stages:` + `image:`/`script:` + no `pool:` → GitLab; `pool:`/`trigger:`/`stages:` with `- stage:` blocks → Azure). Ambiguous → `null`.
   - `Convert(string sourcePath, SourceFormat format)` → `ConversionOutcome { Pipeline? Pipeline, IReadOnlyList<string> Warnings, string? Error }` (Error = not-CI-YAML / no convertible jobs). Warnings carry source context (`"job 'build': 'runs-on' cannot be mapped"`).
   - Parse source with YamlDotNet into **loose `YamlNode`/dynamic POCOs per format** (source schemas differ too much for one POCO; `IgnoreUnmatchedProperties` won't cut it). Document mapping table on the interface (XML docs — `csharp-docs` skill).
3. Register in `AddEngine`.
4. **Acceptance:** unit tests: detection per format + ambiguous → null; per-format mapping (GH `needs`→`depends_on`, GL script, ADO stage deps, env/variables, timeouts); unconvertible input → `Error`.

## Step 2 — Engine: writer + orchestration
1. `PipelineHelper.Write(Pipeline, string path)` — serializer with underscored naming, EF-prop ignores, omit defaults; round-trip verified (parse the written file back).
2. `Engine/Orchestration/Interfaces/IPipelineConverter.cs` + `Engine/Orchestration/PipelineConverter.cs`:
   - `ConvertAsync(string sourcePath, SourceFormat? formatHint, string cwd, string? outputName, bool force, ct)` → `ConversionResult { SourceFormat Format, string OutputPath, IReadOnlyList<string> Warnings, IReadOnlyList<string> ValidationErrors, bool Succeeded }`.
   - Flow: resolve repo from `cwd` (`NotARailwayRepositoryException` if missing) → detect format (use hint if given; if still null → return `FormatUndetermined` marker/flag — **CLI does the prompting**, Engine never prompts) → `IPipelineConvertService.Convert` → derive output name (`Path.GetFileNameWithoutExtension` + `.yaml`; `--output` wins; append `.yaml` if missing) → refuse existing target unless `force` (domain behavior: return flag or throw — prefer thrown `InvalidOperationException` with clear message, rendered by handler) → write via `PipelineHelper.Write` → `IPipelineValidator.ValidateAsync(writtenPath)` → return result with `ValidationErrors`.
   - Register in `AddEngine`.
3. **Acceptance:** integration test in temp git+`.railway` repo: GH workflow fixture → result `Succeeded`, file exists and re-parses, validation errors empty; overwrite without force → error; validation-failing fixture → `ValidationErrors` populated and file still exists (deletion decision belongs to CLI).

## Step 3 — CLI command + handler
1. `Cli/Handlers/Interfaces/IConvertHandler.cs` + `Cli/Handlers/ConvertHandler.cs` (renderer):
   - Format undetermined: if interactive → Spectre `SelectionPrompt<SourceFormat>`; else → error `Cannot detect pipeline format; pass --format github|gitlab|azure` (rendered, exit 1).
   - Hard failure (`Error != null`, repo missing, overwrite): render red + rethrow BCL exception (RunHandler pattern).
   - Success path: green `✓ Converted + validated → <path>`, yellow **Warnings** section (each line), then job tree (mirror `ValidationHandler`). If `ValidationErrors` non-empty: red errors → `AnsiConsole.Confirm("Keep the file anyway?", defaultAnswer: false)` → false: delete file, render `Deleted <path>`; true: render `Kept <path>`. Return outcome enum/bool; command maps to exit codes.
2. `Cli/Commands/ConvertCommand.cs` — `ConvertSettings { [CommandArgument(0,"<PATH>")] SourceFile, [CommandOption("--format")] SourceFormat? Format, [--output], [--force] }`; catch `NotARailwayRepositoryException` → message + 1; generic catch → 1 (only when not already handled). Exit: 0 clean, 1 everything else per Decisions table.
3. Register handler + `AddCommand<ConvertCommand>("convert").WithDescription("Convert a GitHub Actions / GitLab / Azure DevOps YAML file into a Railway pipeline.")` in `Cli/Program.cs`.
4. **Acceptance:** `railway convert` with GH fixture in temp repo prints warnings tree, file lands in `.railway/pipelines/`, exit 0; `--format` required message when non-interactive + undetectable.

## Step 4 — Tests
1. `Tests/Unit/` — `PipelineFormatDetectorTests`, `PipelineConvertServiceTests` (per format + warning coverage), `PipelineConverterTests` (mock `IPipelineValidator`, `IPipelineConvertService`; force/overwrite, format-hint pass-through, validation errors propagated).
2. `Tests/Integration/ConvertCommandTests.cs` (temp git repo + temp SQLite, `[Collection("Integration Tests")]`): happy path GH fixture (file written, exit 0), overwrite refusal (exit 1), `--force`, validation-failure path (tests run non-interactive → file kept, exit 1), ambiguous input without `--format` (exit 1, message).
3. Ensure existing suite still green.
4. **Acceptance:** `dotnet test` passes.

## Verification (per AGENTS.md)
```bash
dotnet build Railway.slnx
dotnet test
```
Manual spot-check: `railway convert <sample gh workflow> ` inside a `.railway` repo; confirm warnings are yellow, validation runs, exit codes match the Decisions table.

## Out of scope
- MCP tool (`Mcp/` untouched — `EngineExceptionMapper` unchanged).
- Round-trip reverse conversion (Railway → GH), matrix expansion, action resolution (`uses:`), trigger mapping.
