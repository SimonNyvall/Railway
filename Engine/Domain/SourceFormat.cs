namespace Railway.Engine.Domain;

/// <summary>
/// Identifies the source CI system that a pipeline file was authored for.
/// </summary>
/// <remarks>
/// Member names are chosen so that a Spectre CLI option (<c>--format github|gitlab|azure</c>) binds to them case-insensitively.
/// </remarks>
public enum SourceFormat
{
    /// <summary>
    /// A GitHub Actions workflow, typically under <c>.github/workflows/</c>.
    /// </summary>
    GitHub,

    /// <summary>
    /// A GitLab CI configuration, typically <c>.gitlab-ci.yml</c>.
    /// </summary>
    GitLab,

    /// <summary>
    /// An Azure DevOps pipeline, typically <c>azure-pipelines.yml</c>.
    /// </summary>
    Azure
}
