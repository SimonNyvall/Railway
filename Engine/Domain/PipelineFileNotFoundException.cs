namespace Railway.Engine.Domain;

public sealed class PipelineFileNotFoundException : Exception
{
    public string FileName { get; }

    public PipelineFileNotFoundException(string fileName)
        : base($"Pipeline file not found: {fileName}")
    {
        FileName = fileName;
    }
}
