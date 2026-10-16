namespace Railway.Engine.Domain;

/// <summary>
/// Represents the outcome of converting a foreign CI pipeline file into a Railway pipeline file on disk.
/// </summary>
/// <param name="Status">The outcome category of the attempt.</param>
/// <param name="Format">
/// The source format that was converted, or <see langword="null"/> when <see cref="Status"/> is
/// <see cref="ConversionStatus.FormatUndetermined"/>.
/// </param>
/// <param name="OutputPath">
/// The full path of the written Railway pipeline file, or <see langword="null"/> when nothing was written.
/// </param>
/// <param name="Error">
/// The reason the source could not be converted, or <see langword="null"/> when the source produced a pipeline.
/// </param>
/// <param name="Warnings">The source constructs that could not be mapped, each carrying its source context.</param>
/// <param name="ValidationErrors">
/// The errors reported by the pipeline validator for the written file, or an empty list when none were reported.
/// </param>
/// <remarks>
/// <para>
/// <see cref="Status"/> distinguishes the three terminal outcomes so the caller can react appropriately:
/// <see cref="ConversionStatus.Succeeded"/> ships a pipeline that was written and validated;
/// <see cref="ConversionStatus.FormatUndetermined"/> asks the caller to choose a format (the engine never prompts);
/// and <see cref="ConversionStatus.ConversionFailed"/> reports a source that could not be converted.
/// </para>
/// <para>
/// <see cref="Succeeded"/> reports only whether conversion and writing completed. It remains
/// <see langword="true"/> when <see cref="ValidationErrors"/> is non-empty: deciding whether to keep or delete a file
/// that failed validation belongs to the caller.
/// </para>
/// </remarks>
public sealed record ConversionResult(
    ConversionStatus Status,
    SourceFormat? Format,
    string? OutputPath,
    string? Error,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ValidationErrors)
{
    /// <summary>
    /// Gets a value that indicates whether the source was converted and written to disk.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when conversion and writing succeeded, even if <see cref="ValidationErrors"/> is
    /// non-empty; otherwise, <see langword="false"/>. The default is <see langword="false"/> for every status other
    /// than <see cref="ConversionStatus.Succeeded"/>.
    /// </value>
    public bool Succeeded => Status == ConversionStatus.Succeeded;
}
