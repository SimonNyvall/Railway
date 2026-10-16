using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration.Interfaces;

/// <summary>
/// Converts a GitHub Actions, GitLab CI, or Azure DevOps pipeline file into a Railway pipeline file under
/// <c>.railway/pipelines/</c> and validates the written file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flow:</b> resolve the repository from <c>cwd</c> (the engine never reads the process working directory) →
/// determine the source format (the supplied hint wins, otherwise content/path detection runs) → map the source with
/// <c>IPipelineConvertService</c> → derive the output file name → refuse an existing target unless overwriting is
/// allowed → write the pipeline → validate the written file.
/// </para>
/// <para>
/// <b>Format-undetermined handling:</b> when neither the hint nor detection yields a format, the method does not throw
/// and does not prompt; it returns a <see cref="ConversionResult"/> whose <see cref="ConversionResult.Status"/> is
/// <see cref="ConversionStatus.FormatUndetermined"/> and whose <see cref="ConversionResult.Format"/> is
/// <see langword="null"/>. The caller decides whether to prompt interactively or fail with a message.
/// </para>
/// <para>
/// <b>Overwrite handling:</b> when the target file already exists and overwriting is not allowed, the method throws
/// <see cref="InvalidOperationException"/>. The caller renders the message.
/// </para>
/// <para>
/// <b>Success semantics:</b> <see cref="ConversionResult.Succeeded"/> is <see langword="true"/> once the source is
/// converted and written, even when <see cref="ConversionResult.ValidationErrors"/> is non-empty. Deleting or keeping
/// a file that failed validation is the caller's decision.
/// </para>
/// <para>
/// <b>Warnings</b> are inherited verbatim from <c>IPipelineConvertService</c>; see its documentation for the v1
/// mapping table, the stage-dependency heuristic, and the constructs that are reported rather than mapped.
/// </para>
/// </remarks>
public interface IPipelineConverter
{
    /// <summary>
    /// Converts <paramref name="sourcePath"/> into a Railway pipeline file under
    /// <c>.railway/pipelines/</c> and validates the result.
    /// </summary>
    /// <param name="sourcePath">
    /// The path to the source pipeline file. A relative path is resolved against <paramref name="cwd"/>.
    /// </param>
    /// <param name="formatHint">
    /// One of the enumeration values that specifies the source format explicitly, or <see langword="null"/> to detect
    /// the format from the file's path and content.
    /// </param>
    /// <param name="cwd">The directory to start repository resolution from.</param>
    /// <param name="outputName">
    /// The optional file name to write under <c>.railway/pipelines/</c>. When <see langword="null"/>, the source file's
    /// name is used. A <c>.yaml</c> extension is appended when the name has no extension.
    /// </param>
    /// <param name="force">
    /// <see langword="true"/> to overwrite an existing target file; otherwise, <see langword="false"/>.
    /// </param>
    /// <param name="ct">The token to monitor for cancellation requests.</param>
    /// <returns>
    /// A result describing the converted pipeline, the warnings it produced, and any validation errors for the written
    /// file; or a result reporting that the format is undetermined or the source could not be converted.
    /// </returns>
    /// <exception cref="NotARailwayRepositoryException">
    /// No <c>.railway</c> folder (or no enclosing git repository) was found at or above <paramref name="cwd"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The target file already exists and <paramref name="force"/> is <see langword="false"/>.
    /// </exception>
    Task<ConversionResult> ConvertAsync(
        string sourcePath,
        SourceFormat? formatHint,
        string cwd,
        string? outputName,
        bool force,
        CancellationToken ct = default);
}
