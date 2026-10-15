using Railway.Engine.Helpers;

using Xunit;

namespace Tests.Unit;

public class DatabasePathHelperTests
{
    [Fact]
    public void GetForPlatform_Windows_UsesLocalApplicationData()
    {
        var path = DatabasePathHelper.GetForPlatform(DatabasePlatform.Windows);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(localAppData, "Railway"), path);
        Assert.Equal("Railway", Path.GetFileName(path));
    }

    [Fact]
    public void GetForPlatform_Linux_UsesXdgDataHomeOrLocalShare()
    {
        var path = DatabasePathHelper.GetForPlatform(DatabasePlatform.Linux);

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expectedBase = xdgDataHome ?? Path.Combine(userProfile, ".local", "share");

        Assert.Equal(Path.Combine(expectedBase, "Railway"), path);
        Assert.Equal("Railway", Path.GetFileName(path));
    }

    [Fact]
    public void GetForPlatform_MacOS_UsesLibraryApplicationSupport()
    {
        var path = DatabasePathHelper.GetForPlatform(DatabasePlatform.MacOS);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(userProfile, "Library", "Application Support", "Railway");

        Assert.Equal(expected, path);
        Assert.EndsWith(Path.Combine("Library", "Application Support", "Railway"), path);
    }

    [Fact]
    public void GetForPlatform_UnknownPlatform_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DatabasePathHelper.GetForPlatform((DatabasePlatform)42));
    }

    [Fact]
    public void Get_DispatchesToTheCurrentOperatingSystemBranch()
    {
        var expected = OperatingSystem.IsWindows()
            ? DatabasePathHelper.GetForPlatform(DatabasePlatform.Windows)
            : OperatingSystem.IsLinux()
                ? DatabasePathHelper.GetForPlatform(DatabasePlatform.Linux)
                : OperatingSystem.IsMacOS()
                    ? DatabasePathHelper.GetForPlatform(DatabasePlatform.MacOS)
                    : throw new InvalidOperationException("Unsupported operating system for this test.");

        Assert.Equal(expected, DatabasePathHelper.Get());
    }
}
