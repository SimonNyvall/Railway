using Railway.Engine.Domain;

namespace Railway.Engine.Orchestration;

public sealed record ValidationResult(Pipeline Pipeline, IReadOnlyList<string> Errors);
