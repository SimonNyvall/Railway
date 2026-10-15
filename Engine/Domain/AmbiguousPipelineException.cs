namespace Railway.Engine.Domain;

public sealed class AmbiguousPipelineException : Exception
{
    public string FileName { get; }

    public IReadOnlyList<string> Candidates { get; }

    public AmbiguousPipelineException(string fileName, IReadOnlyList<string> candidates)
        : base($"Ambiguous pipeline file name: {fileName}")
    {
        FileName = fileName;
        Candidates = candidates;
    }
}
