namespace Cli.Helpers;

public static class DatabasePathHelper
{
	public static string Get()
	{
		var path = string.Empty;
		const string applicationFolder = "Railway";
		
		if (OperatingSystem.IsWindows())
			path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		else if (OperatingSystem.IsLinux())
			path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "something");
		else if (OperatingSystem.IsMacOS())
			path = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
		
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
