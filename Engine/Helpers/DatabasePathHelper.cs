namespace Railway.Engine.Helpers;

/// <summary>
/// Resolves the platform-specific application-data directory that holds the
/// local Railway SQLite database.
/// </summary>
public static class DatabasePathHelper
{
	/// <summary>
	/// Gets the Railway application-data directory for the current operating system.
	/// </summary>
	/// <returns>The platform-specific path, ending in <c>Railway</c>.</returns>
	/// <exception cref="InvalidOperationException">
	/// Thrown when the current operating system is not supported.
	/// </exception>
	public static string Get()
	{
		if (OperatingSystem.IsWindows())
			return GetForPlatform(DatabasePlatform.Windows);

		if (OperatingSystem.IsLinux())
			return GetForPlatform(DatabasePlatform.Linux);

		if (OperatingSystem.IsMacOS())
			return GetForPlatform(DatabasePlatform.MacOS);

		throw new InvalidOperationException("Could not find the os of the user");
	}

	/// <summary>
	/// Gets the Railway application-data directory for an explicit platform.
	/// </summary>
	/// <remarks>
	/// This is the testability seam for <see cref="Get"/>: the public method maps
	/// the running OS onto a <see cref="DatabasePlatform"/> value via
	/// <see cref="OperatingSystem.IsWindows"/>, <see cref="OperatingSystem.IsLinux"/>
	/// and <see cref="OperatingSystem.IsMacOS"/>, while the unit tests call this
	/// overload directly to exercise every branch regardless of the host OS. The
	/// lookup rules are:
	/// <list type="bullet">
	/// <item><description>Windows: <see cref="System.Environment.SpecialFolder.LocalApplicationData"/>.</description></item>
	/// <item><description>Linux: the <c>XDG_DATA_HOME</c> environment variable, falling back to <c>~/.local/share</c>.</description></item>
	/// <item><description>macOS: <c>~/Library/Application Support</c>.</description></item>
	/// </list>
	/// </remarks>
	/// <param name="platform">The platform whose data directory should be resolved.</param>
	/// <returns>The platform-specific path, ending in <c>Railway</c>.</returns>
	/// <exception cref="InvalidOperationException">
	/// Thrown when <paramref name="platform"/> is not a recognised value.
	/// </exception>
	internal static string GetForPlatform(DatabasePlatform platform)
	{
		const string applicationFolder = "Railway";

		var path = platform switch
		{
			DatabasePlatform.Windows => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			DatabasePlatform.Linux => Environment.GetEnvironmentVariable("XDG_DATA_HOME")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
			DatabasePlatform.MacOS => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support"),
			_ => string.Empty
		};

		return string.IsNullOrEmpty(path)
			? throw new InvalidOperationException("Could not find the os of the user")
			: Path.Combine(path, applicationFolder);
	}

	public static bool CreatePathIfNotExist(string path)
	{
		try
		{
			if (!Path.Exists(path))
			{
				Directory.CreateDirectory(path);
			}
			return true;	
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) 
		{
			return false;
		}
	}
}

/// <summary>
/// The operating systems <see cref="DatabasePathHelper"/> knows how to resolve a
/// data directory for.
/// </summary>
internal enum DatabasePlatform
{
	/// <summary>Microsoft Windows.</summary>
	Windows,

	/// <summary>Linux (and other XDG-compliant Unix systems).</summary>
	Linux,

	/// <summary>Apple macOS.</summary>
	MacOS
}
