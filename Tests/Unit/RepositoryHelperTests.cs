using Railway.Engine.Domain;
using Railway.Engine.Helpers;
using Xunit;

namespace Tests.Unit;

/// <summary>
/// Unit tests for <see cref="RepositoryHelper"/> proving that repository
/// discovery works from an explicit <c>startPath</c> without depending on the
/// process working directory.
/// </summary>
[Collection("Integration Tests")]
public class RepositoryHelperTests
{
    [Fact]
    public void FindUpwards_ReturnsFirstAncestorMatchingPredicate()
    {
        var tempDir = CreateTempDir();
        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            var nested = Path.Combine(repoRoot, "src", "nested");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

            var found = RepositoryHelper.FindUpwards(
                nested,
                path => Directory.Exists(Path.Combine(path, ".git")));

            Assert.Equal(Path.GetFullPath(repoRoot), found);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetRootRepositoryPath_FromNestedDirectory_FindsGitFolder()
    {
        var tempDir = CreateTempDir();
        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            var nested = Path.Combine(repoRoot, "src", "nested");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

            var result = RepositoryHelper.GetRootRepositoryPath(nested);

            Assert.Equal(Path.GetFullPath(repoRoot), result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetRailwayPath_FromNestedDirectory_FindsRailwayFolder()
    {
        var tempDir = CreateTempDir();
        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            var nested = Path.Combine(repoRoot, "src", "nested");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(repoRoot, ".railway"));

            var result = RepositoryHelper.GetRailwayPath(nested);

            Assert.Equal(Path.Combine(Path.GetFullPath(repoRoot), ".railway"), result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetRailwayPathOrThrow_NoAncestorRailwayFolder_Throws()
    {
        var tempDir = CreateTempDir();
        try
        {
            var nested = Path.Combine(tempDir, "no-repo", "nested");
            Directory.CreateDirectory(nested);

            Assert.Throws<NotARailwayRepositoryException>(
                () => RepositoryHelper.GetRailwayPathOrThrow(nested));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Discovery_DoesNotDependOnCurrentDirectory()
    {
        var tempDir = CreateTempDir();
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            var repoRoot = Path.Combine(tempDir, "repo");
            var nested = Path.Combine(repoRoot, "src");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(repoRoot, ".railway"));

            // Point the process somewhere unrelated to prove the explicit path wins.
            Directory.SetCurrentDirectory(Path.GetTempPath());

            var railwayPath = RepositoryHelper.GetRailwayPath(nested);

            Assert.Equal(Path.Combine(Path.GetFullPath(repoRoot), ".railway"), railwayPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
            Directory.Delete(tempDir, true);
        }
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "railway_repohelper_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }
}
