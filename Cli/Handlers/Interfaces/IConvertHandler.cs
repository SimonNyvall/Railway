using Railway.Engine.Domain;

namespace Cli.Handlers.Interfaces;

/// <summary>
/// Renders the outcome of converting a GitHub Actions, GitLab CI, or Azure DevOps pipeline file into a Railway
/// pipeline file under <c>.railway/pipelines/</c>.
/// </summary>
/// <remarks>
/// <para>
/// The handler is a renderer over <c>IPipelineConverter</c>. It resolves the current working directory itself (the
/// engine never reads it), maps the conversion result onto console output, and reports whether the invocation should
/// exit successfully.
/// </para>
/// <para>
/// <b>Format selection:</b> when the engine cannot determine the source format (status
/// <see cref="ConversionStatus.FormatUndetermined"/> or a <see langword="null"/> format), the handler prompts with a
/// case-preserving <c>SelectionPrompt&lt;SourceFormat&gt;</c> when the console is interactive and retries the
/// conversion with the chosen format. When the console is non-interactive it renders
/// <c>Cannot detect pipeline format; pass --format github|gitlab|azure</c> and reports failure instead of prompting.
/// </para>
/// <para>
/// <b>Hard failures:</b> when the engine reports an error (unrecognizable source, no convertible jobs) or throws for a
/// non-recoverable condition (missing repository, refusing to overwrite an existing target), the handler renders the
/// failure in red and rethrows a BCL exception so the command's generic catch maps it to exit code 1.
/// </para>
/// <para>
/// <b>Warnings:</b> source constructs that could not be mapped are rendered under a yellow <b>Warnings</b> heading.
/// They never affect the outcome: a conversion that produced a valid file still succeeds.
/// </para>
/// <para>
/// <b>Validation:</b> the engine validates the written file and returns any errors. The handler renders them in red and
/// asks <c>Keep the file anyway?</c> (default: no) when interactive. Answering no deletes the file and renders
/// <c>Deleted &lt;path&gt;</c>; answering yes keeps it and renders <c>Kept &lt;path&gt;</c>. A non-interactive console
/// keeps the file without prompting. Either way the handler reports failure, because the emitted pipeline is invalid.
/// </para>
/// <para>
/// <b>Success:</b> a converted file that passes validation renders the green
/// <c>✓ Converted + validated → &lt;path&gt;</c> line, the yellow warnings (if any), and the same job tree
/// <c>validate</c> prints.
/// </para>
/// </remarks>
public interface IConvertHandler
{
    /// <summary>
    /// Converts the pipeline file at <paramref name="sourcePath"/> and renders the result.
    /// </summary>
    /// <param name="sourcePath">
    /// The path to the source CI pipeline YAML file. A relative path is resolved against the current working directory,
    /// which the handler supplies to the engine.
    /// </param>
    /// <param name="format">
    /// One of the enumeration values that specifies the source format explicitly, or <see langword="null"/> to let the
    /// engine detect it (and to prompt for it interactively when detection fails).
    /// </param>
    /// <param name="outputName">
    /// The optional file name to write under <c>.railway/pipelines/</c>, or <see langword="null"/> to derive the name
    /// from <paramref name="sourcePath"/>.
    /// </param>
    /// <param name="force">
    /// <see langword="true"/> to overwrite an existing target file; otherwise, <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the source was converted and the written file passed validation (exit code 0);
    /// otherwise, <see langword="false"/>, which includes a non-interactive format-detection failure and a written file
    /// that failed validation (exit code 1).
    /// </returns>
    /// <exception cref="NotARailwayRepositoryException">
    /// No <c>.railway</c> folder (or no enclosing git repository) was found at or above the current working directory.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The source could not be converted, or the target file already exists and <paramref name="force"/> is
    /// <see langword="false"/>.
    /// </exception>
    Task<bool> Handle(string sourcePath, SourceFormat? format, string? outputName, bool force);
}
