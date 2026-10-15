using Railway.Engine.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Tests.Infrastructure;

public class TestDatabaseFixture : IDisposable
{
    public string DatabasePath { get; }
    public DbContextOptions<RailwayDbContext> Options { get; }

    public TestDatabaseFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), "railway_test_db_" + Guid.NewGuid());
        Directory.CreateDirectory(DatabasePath);

        Options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={DatabasePath}/data.db")
            .Options;

        using var context = new RailwayDbContext(Options);
        context.Database.EnsureCreated();
    }

    public RailwayDbContext CreateContext() => new RailwayDbContext(Options);

    public void Dispose()
    {
        if (Directory.Exists(DatabasePath))
        {
            Directory.Delete(DatabasePath, true);
        }
    }
}

public class TempGitRepository : IDisposable
{
    public string Path { get; }
    private readonly string _originalDir;

    public TempGitRepository()
    {
        _originalDir = Directory.GetCurrentDirectory();
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "railway_test_repo_" + Guid.NewGuid());
        Directory.CreateDirectory(Path);
        Directory.SetCurrentDirectory(Path);

        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test User");
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }

    public void RunGit(string args)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        process.WaitForExit();
    }
}

public class TestRailwayRepository : IDisposable
{
    public string Path { get; }
    private readonly string _originalDir;
    private readonly TempGitRepository _gitRepo;

    public TestRailwayRepository()
    {
        _originalDir = Directory.GetCurrentDirectory();
        _gitRepo = new TempGitRepository();
        Path = _gitRepo.Path;
        
        // Initialize railway
        Directory.SetCurrentDirectory(Path);
        InitializeRailway();
    }

    private void InitializeRailway()
    {
        var railwayPath = System.IO.Path.Combine(Path, ".railway");
        Directory.CreateDirectory(railwayPath);
        Directory.CreateDirectory(System.IO.Path.Combine(railwayPath, "pipelines"));
        
        var manifestPath = System.IO.Path.Combine(railwayPath, "railway.yaml");
        if (!File.Exists(manifestPath))
        {
            var manifestContent = "name: " + new DirectoryInfo(Path).Name + "\npipelines: []\n";
            File.WriteAllText(manifestPath, manifestContent);
        }
    }

    public string GetPipelinePath(string fileName)
    {
        return System.IO.Path.Combine(Path, ".railway", "pipelines", fileName);
    }

    public void WritePipeline(string fileName, string content)
    {
        var pipelinePath = GetPipelinePath(fileName);
        File.WriteAllText(pipelinePath, content);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        _gitRepo.Dispose();
    }
}
