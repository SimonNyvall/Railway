namespace Tests.Infrastructure;

/// <summary>
/// Creates a uniquely named temporary directory that is recursively deleted when
/// disposed.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>Gets the full path of the temporary directory.</summary>
    public string Path { get; }

    /// <summary>Initializes a new instance of the <see cref="TempDirectory"/> class.</summary>
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("railway_test_").FullName;
    }

    /// <summary>Gets the full path of a file under this directory with the given name.</summary>
    /// <param name="fileName">The name of the file.</param>
    /// <returns>The combined full path.</returns>
    public string File(string fileName) => System.IO.Path.Combine(Path, fileName);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
