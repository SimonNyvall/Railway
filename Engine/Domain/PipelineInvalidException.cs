namespace Railway.Engine.Domain;

public sealed class PipelineInvalidException : Exception
{
    public string FileName { get; }

    public IReadOnlyList<string> Errors { get; }

    public PipelineInvalidKind Kind { get; }

    public PipelineInvalidException(string fileName, IReadOnlyList<string> errors, PipelineInvalidKind kind)
        : base(kind == PipelineInvalidKind.Parse
            ? $"Pipeline parse failed: {FirstError(errors)}"
            : "Pipeline validation failed")
    {
        FileName = fileName;
        Errors = errors;
        Kind = kind;
    }

    private static string FirstError(IReadOnlyList<string> errors) =>
        errors.Count > 0 ? errors[0] : string.Empty;
}
