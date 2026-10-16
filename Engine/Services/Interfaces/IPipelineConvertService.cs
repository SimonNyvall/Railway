using Railway.Engine.Domain;

namespace Railway.Engine.Services.Interfaces;

/// <summary>
/// Converts GitHub Actions, GitLab CI, or Azure DevOps pipeline YAML into a Railway <see cref="Pipeline"/> on a best-effort basis.
/// </summary>
/// <remarks>
/// <para>
/// The service performs no I/O beyond reading the source file. Every source construct that has no Railway
/// equivalent is reported as a warning carrying its source context, for example
/// <c>job 'build': 'runs-on' cannot be mapped</c>; nothing encountered is dropped silently.
/// </para>
/// <para>
/// <b>v1 mapping table</b> (everything not listed produces a warning):
/// </para>
/// <example>
/// <code language="text">
/// Source                                Railway target
/// ------------------------------------  -------------------------------------------------
/// GH  jobs.&lt;id&gt;                          job key = &lt;id&gt;
/// GL  top-level mapping keys            job key = top-level key
/// ADO - job: inside - stage:/- jobs:    job key = 'job' value
/// GH  needs                              depends_on (missing targets dropped + warning)
/// GL  stages: order                      depends_on = all jobs of the previous stage
/// ADO - stage: order                     depends_on = all jobs of the previous stage
/// GH  steps[].run                        step.run  (one Railway step per list entry)
/// GL  script: list                       step.run  (one Railway step per script list entry)
/// ADO script:                            step.run  (scalar = one step; list = one per entry)
/// GH  steps[].name / ADO displayName     step.name (job identity stays the job key)
/// GH  env (workflow) / GL, ADO variables pipeline.variables (mapping or - name/value list)
/// job-level env / variables              job.env
/// step-level env                         step.env
/// GH  timeout-minutes / ADO              job.timeout (TimeSpan.FromMinutes)
///     timeoutInMinutes (job)
/// GH  timeout-minutes / ADO              step.timeout (TimeSpan.FromMinutes)
///     timeoutInMinutes (step)
/// </code>
/// </example>
/// <para>
/// <b>Stage dependency heuristic:</b> GitLab <c>stages:</c> order and Azure DevOps <c>- stage:</c> order are
/// flattened into <see cref="Job.DependsOn"/>: every job depends on all jobs of the immediately previous stage.
/// First-stage jobs — and jobs whose stage is missing or not listed in <c>stages:</c> — get an empty
/// <see cref="Job.DependsOn"/>; so do jobs whose previous stage contributed no convertible job. Azure DevOps
/// pipelines declaring a top-level <c>jobs:</c> sequence (no stages) produce jobs without dependencies.
/// GitHub Actions <c>needs</c> entries pointing at jobs that were not converted are dropped with a warning.
/// </para>
/// <para>
/// <b>Skipped jobs:</b> a job that yields no step with a non-empty run command (for example a GitHub job that
/// only declares <c>uses:</c>, or a GitLab job that only declares <c>trigger:</c>) is skipped with a warning.
/// When no job survives, <see cref="ConversionOutcome.Error"/> is set instead of a pipeline.
/// </para>
/// <para>
/// <b>Warnings</b> (one per construct encountered, naming the job/step and the key) cover at least:
/// <c>uses:</c>, <c>runs-on</c>, <c>pool:</c>, <c>matrix</c>/<c>strategy</c>, <c>container</c>, <c>services</c>,
/// <c>with:</c>/<c>args</c>, triggers (<c>on:</c>, <c>trigger:</c>, <c>pr:</c>), <c>if:</c>, <c>conditions:</c>,
/// <c>permissions</c>, <c>continue-on-error</c>, <c>rules:</c>/<c>only:</c>/<c>when:</c>, GitLab <c>image:</c>,
/// YAML anchors (merge keys appear as <c>&lt;&lt;</c>) and <c>!reference</c> values, <c>defaults</c>,
/// <c>environment:</c>, concurrency, GitLab <c>needs</c> and Azure <c>dependsOn</c> (the stage heuristic is
/// used instead), plus any key not present in the mapping table.
/// </para>
/// </remarks>
public interface IPipelineConvertService
{
    /// <summary>
    /// Detects which CI system authored the pipeline file at <paramref name="sourcePath"/>.
    /// </summary>
    /// <param name="sourcePath">The path to the source pipeline YAML file.</param>
    /// <returns>
    /// One of the enumeration values identifying the source format, or <see langword="null"/> when no heuristic
    /// matches (failed), the file cannot be read or parsed, or the content matches more than one format (ambiguous).
    /// </returns>
    /// <remarks>
    /// Path heuristics run first and win immediately: a path containing <c>.github/workflows/</c> identifies
    /// <see cref="SourceFormat.GitHub"/>, a file name starting with <c>.gitlab-ci</c> identifies
    /// <see cref="SourceFormat.GitLab"/>, and a file name starting with <c>azure-pipelines</c> identifies
    /// <see cref="SourceFormat.Azure"/>. Otherwise the content is evaluated:
    /// <list type="bullet">
    /// <item><description>Root <c>on:</c> plus <c>runs-on</c> anywhere → <see cref="SourceFormat.GitHub"/>.</description></item>
    /// <item><description><c>pool:</c> anywhere, root <c>trigger:</c>, or root <c>stages:</c> containing <c>- stage:</c> blocks → <see cref="SourceFormat.Azure"/>.</description></item>
    /// <item><description>Root <c>stages:</c> with plain-string entries plus <c>script:</c>/<c>image:</c> anywhere and no <c>pool:</c> → <see cref="SourceFormat.GitLab"/>.</description></item>
    /// </list>
    /// When zero or multiple formats match, the result is <see langword="null"/> (the caller decides how to proceed; this service never prompts).
    /// </remarks>
    SourceFormat? DetectFormat(string sourcePath);

    /// <summary>
    /// Converts the CI pipeline file at <paramref name="sourcePath"/> into a Railway pipeline, best-effort.
    /// </summary>
    /// <param name="sourcePath">The path to the source pipeline YAML file.</param>
    /// <param name="format">One of the enumeration values that specifies the source format to convert from.</param>
    /// <returns>
    /// An outcome carrying the converted pipeline and its warnings on success, or an <see cref="ConversionOutcome.Error"/>
    /// describing why the source is not convertible; the method does not throw for unconvertible input.
    /// </returns>
    /// <remarks>
    /// <see cref="ConversionOutcome.Error"/> is set when the file is missing, unreadable, not valid YAML, not a
    /// recognizable CI document for <paramref name="format"/>, or contains no convertible jobs; in that case
    /// <see cref="ConversionOutcome.Pipeline"/> is <see langword="null"/>. Warnings accumulated up to the error are retained.
    /// </remarks>
    ConversionOutcome Convert(string sourcePath, SourceFormat format);
}
