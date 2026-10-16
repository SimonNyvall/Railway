namespace Railway.Engine.Domain;

/// <summary>
/// Describes the outcome category of an attempt to convert a foreign CI pipeline file into a Railway pipeline.
/// </summary>
/// <remarks>
/// <see cref="Succeeded"/> is deliberately not the zero value so that an uninitialized <see cref="ConversionStatus"/>
/// cannot read as a successful conversion (the same pitfall the codebase documents for <c>RunStatus.Succeeded = 0</c>).
/// </remarks>
public enum ConversionStatus
{
    /// <summary>
    /// The source format could not be determined and no format hint was supplied, so nothing was written.
    /// The caller must either prompt for a format or fail; the engine never prompts.
    /// </summary>
    FormatUndetermined,

    /// <summary>
    /// The source could not be converted into a Railway pipeline, so nothing was written.
    /// </summary>
    ConversionFailed,

    /// <summary>
    /// The source was converted and written to disk. Validation may still report errors; see
    /// <see cref="ConversionResult.ValidationErrors"/>.
    /// </summary>
    Succeeded
}

