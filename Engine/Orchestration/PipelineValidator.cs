using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

public sealed class PipelineValidator : IPipelineValidator
{
    private readonly IValidateService _validateService;

    public PipelineValidator(IValidateService validateService)
    {
        _validateService = validateService;
    }

    public async Task<ValidationResult> ValidateAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsFileYaml(filePath))
        {
            throw new ArgumentException("Not a yaml file.");
        }

        if (!Path.Exists(filePath))
        {
            throw new ArgumentException("Not a valid path.");
        }

        var pipeline = await PipelineHelper.Parse(filePath);

        var errors = (await _validateService.IsValid(pipeline)).ToList();

        return new ValidationResult(pipeline, errors);
    }

    private static bool IsFileYaml(string path)
    {
        return path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
    }
}
