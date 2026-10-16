namespace Railway.Engine.Domain;

/// <summary>
/// Represents the result of converting a foreign CI pipeline file into a <see cref="Pipeline"/>.
/// </summary>
/// <param name="Pipeline">The converted pipeline, or <see langword="null"/> when <paramref name="Error"/> is set.</param>
/// <param name="Warnings">The constructs that could not be mapped, each carrying its source context.</param>
/// <param name="Error">The reason the source could not be converted, or <see langword="null"/> when conversion succeeded.</param>
public sealed record ConversionOutcome(Pipeline? Pipeline, IReadOnlyList<string> Warnings, string? Error);
