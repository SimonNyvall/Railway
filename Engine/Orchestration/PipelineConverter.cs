using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Railway.Engine.Orchestration.Interfaces;
using Railway.Engine.Services.Interfaces;

namespace Railway.Engine.Orchestration;

/// <inheritdoc/>
public sealed class PipelineConverter : IPipelineConverter
{
    private readonly IPipelineConvertService _convertService;
    private readonly IPipelineValidator _validator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineConverter"/> class.
    /// </summary>
    /// <param name="convertService">The service that detects the source format and maps the source into a pipeline.</param>
    /// <param name="validator">The validator that checks the written pipeline file.</param>
    public PipelineConverter(IPipelineConvertService convertService, IPipelineValidator validator)
    {
        _convertService = convertService;
        _validator = validator;
    }

    /// <inheritdoc/>
    public async Task<ConversionResult> ConvertAsync(
        string sourcePath,
        SourceFormat? formatHint,
        string cwd,
        string? outputName,
        bool force,
        CancellationToken ct = default)
    {
        var railwayPath = RepositoryHelper.GetRailwayPathOrThrow(cwd);
        _ = RepositoryHelper.GetRootRepositoryPath(cwd) ?? throw new NotARailwayRepositoryException();

        var resolvedSourcePath = Path.IsPathRooted(sourcePath)
            ? sourcePath
            : Path.GetFullPath(sourcePath, cwd);

        var format = formatHint ?? _convertService.DetectFormat(resolvedSourcePath);
        if (format is null)
        {
            return new ConversionResult(
                ConversionStatus.FormatUndetermined, null, null, null, [], []);
        }

        var outcome = _convertService.Convert(resolvedSourcePath, format.Value);
        if (outcome.Pipeline is null || outcome.Error is not null)
        {
            return new ConversionResult(
                ConversionStatus.ConversionFailed, format, null, outcome.Error, outcome.Warnings, []);
        }

        var targetPath = Path.Combine(
            RepositoryHelper.GetPipelinesPath(railwayPath),
            OutputFileName(resolvedSourcePath, outputName));

        if (File.Exists(targetPath) && !force)
        {
            throw new InvalidOperationException(
                $"'{targetPath}' already exists. Re-run with --force to overwrite it.");
        }

        await PipelineHelper.Write(outcome.Pipeline, targetPath, ct);

        var validation = await _validator.ValidateAsync(targetPath, ct);

        return new ConversionResult(
            ConversionStatus.Succeeded, format, targetPath, null, outcome.Warnings, validation.Errors);
    }

    private static string OutputFileName(string sourcePath, string? outputName)
    {
        if (!string.IsNullOrWhiteSpace(outputName))
        {
            return Path.HasExtension(outputName) ? outputName : outputName + ".yaml";
        }

        return Path.GetFileNameWithoutExtension(sourcePath) + ".yaml";
    }
}
