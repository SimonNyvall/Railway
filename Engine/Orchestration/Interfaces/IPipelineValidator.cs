namespace Railway.Engine.Orchestration.Interfaces;

public interface IPipelineValidator
{
    Task<ValidationResult> ValidateAsync(string filePath, CancellationToken ct = default);
}
