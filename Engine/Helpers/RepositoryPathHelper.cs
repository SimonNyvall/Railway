namespace Railway.Engine.Helpers;

/// <summary>
/// Canonicalizes the string stored in <see cref="Domain.Repository.Path"/>, the identity key used to
/// associate run history with a repository.
/// </summary>
/// <remarks>
/// The stored value is an internal identity only — it is never rendered to users (display names come
/// from <see cref="System.IO.DirectoryInfo.Name"/>) — so lowercasing it on Windows is not user-visible.
/// Windows filesystems are case-insensitive, so two spellings of the same directory (drive letter or
/// user casing) must collapse onto one row; Linux/macOS keep the I/O case so genuinely distinct
/// repositories stay distinct.
/// </remarks>
public static class RepositoryPathHelper
{
    /// <summary>
    /// Returns the canonical form of <paramref name="path"/>: the absolute path with any redundant
    /// trailing separator removed, lowercased on Windows only.
    /// </summary>
    /// <param name="path">The repository path to canonicalize.</param>
    public static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var trimmed = TrimTrailingSeparator(fullPath);

        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var root = Path.GetPathRoot(path);
        if (root is not null && path.Length == root.Length)
        {
            // Never strip a volume/root separator ("/" or "C:\").
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
