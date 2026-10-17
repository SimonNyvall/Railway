using Railway.Engine;
using Railway.Engine.Domain;
using Railway.Engine.Infrastructure;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Tests.Infrastructure;

using Xunit;

namespace Tests.Unit;

public class EngineInitializerTests
{
    private const string RunTable = "Runs";

    [Fact]
    public async Task InitializeEngineAsync_CreatesDatabaseFile()
    {
        using var temp = new TempDirectory();

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = temp.Path });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(temp.File("data.db")), $"Expected data.db to be created in {temp.Path}");
    }

    [Fact]
    public async Task InitializeEngineAsync_CreatesMissingDatabaseDirectory()
    {
        using var temp = new TempDirectory();
        var nestedPath = temp.File("nested");

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = nestedPath });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(nestedPath), $"Expected directory {nestedPath} to be created");
        Assert.True(File.Exists(Path.Combine(nestedPath, "data.db")));
    }

    [Fact]
    public async Task InitializeEngineAsync_SkipsCreation_WhenInitializeDatabaseIsFalse()
    {
        using var temp = new TempDirectory();

        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = temp.Path, InitializeDatabase = false });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(temp.File("data.db")));
    }

    [Fact]
    public async Task InitializeEngineAsync_FreshDatabase_HasRunProvenanceColumnsWithManualDefault()
    {
        using var temp = new TempDirectory();

        await InitializeDatabaseAsync(temp.Path);

        var dbFile = temp.File("data.db");
        var columns = await ReadRunColumnsAsync(dbFile);

        Assert.True(columns.ContainsKey("Trigger"), "Expected the Trigger column to exist");
        Assert.True(columns.ContainsKey("TriggerDetail"), "Expected the TriggerDetail column to exist");

        // The "manual" default is applied by the entity when a Run is written without
        // an explicit trigger; prove it survives a round trip to SQLite.
        var options = new DbContextOptionsBuilder<RailwayDbContext>()
            .UseSqlite($"Data Source={dbFile}")
            .Options;

        int runId;
        await using (var context = new RailwayDbContext(options))
        {
            var repository = new Repository { Path = "/provenance/fresh" };
            context.Repositories.Add(repository);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            var run = new Run
            {
                RepositoryId = repository.Id,
                PipelineFileName = "build.yaml",
                PipelineName = "build",
                StartedAt = DateTimeOffset.UtcNow,
                Status = RunStatus.Succeeded
            };
            context.Runs.Add(run);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            runId = run.Id;
        }

        var (trigger, triggerDetail) = await ReadRunTriggerAsync(dbFile, runId);

        Assert.Equal("manual", trigger);
        Assert.Null(triggerDetail);
    }

    [Fact]
    public async Task InitializeEngineAsync_UpgradesLegacyDatabaseInPlace()
    {
        using var temp = new TempDirectory();
        var dbFile = temp.File("data.db");

        // A database created before this feature...
        await InitializeDatabaseAsync(temp.Path);

        // ...rewound to the pre-shim shape by dropping the columns and inserting a
        // legacy row (SQLite >= 3.35 supports DROP COLUMN; the bundled e_sqlite3 does).
        await ExecuteSqlAsync(
            dbFile,
            $"ALTER TABLE \"{RunTable}\" DROP COLUMN \"Trigger\";",
            $"ALTER TABLE \"{RunTable}\" DROP COLUMN \"TriggerDetail\";",
            "INSERT INTO \"Repositories\" (\"Path\") VALUES ('/legacy/repo');",
            $"INSERT INTO \"{RunTable}\" (\"RepositoryId\", \"PipelineFileName\", \"PipelineName\", \"StartedAt\", \"Status\") " +
            "VALUES (1, 'legacy.yaml', 'legacy', '2024-01-01 00:00:00+00:00', 0);");

        var before = await ReadRunColumnsAsync(dbFile);
        Assert.False(before.ContainsKey("Trigger"), "Precondition: legacy database must not have the Trigger column");
        Assert.False(before.ContainsKey("TriggerDetail"), "Precondition: legacy database must not have the TriggerDetail column");

        // Re-initializing upgrades the existing database in place.
        await InitializeDatabaseAsync(temp.Path);

        var after = await ReadRunColumnsAsync(dbFile);
        Assert.True(after.ContainsKey("Trigger"), "Expected the shim to add the Trigger column");
        Assert.True(after.ContainsKey("TriggerDetail"), "Expected the shim to add the TriggerDetail column");
        Assert.True(after["Trigger"].NotNull, "Trigger must be NOT NULL so existing rows are backfilled");
        Assert.Equal("'manual'", after["Trigger"].DefaultValue);
        Assert.False(after["TriggerDetail"].NotNull);

        // The legacy row survived and was backfilled with the manual default.
        var (trigger, triggerDetail) = await ReadRunTriggerAsync(dbFile, 1);
        Assert.Equal("manual", trigger);
        Assert.Null(triggerDetail);

        var legacyRows = await CountRunsAsync(dbFile);
        Assert.Equal(1, legacyRows);
    }

    [Fact]
    public async Task InitializeEngineAsync_IsIdempotent_WhenProvenanceColumnsAlreadyExist()
    {
        using var temp = new TempDirectory();

        await InitializeDatabaseAsync(temp.Path);
        await InitializeDatabaseAsync(temp.Path);

        var columns = await ReadRunColumnsAsync(temp.File("data.db"));
        Assert.True(columns.ContainsKey("Trigger"));
        Assert.True(columns.ContainsKey("TriggerDetail"));
    }

    [Fact]
    public async Task InitializeEngineAsync_FreshDatabase_HasUniqueRepositoryPathIndex()
    {
        using var temp = new TempDirectory();

        await InitializeDatabaseAsync(temp.Path);

        var dbFile = temp.File("data.db");

        Assert.True(await IndexExistsAsync(dbFile, "IX_Repositories_Path"), "Expected the unique index on Repositories.Path");

        // Enforce it: a second insert of the same path must be rejected.
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteSqlAsync(
            dbFile,
            "INSERT INTO \"Repositories\" (\"Path\") VALUES ('/unique/repo');",
            "INSERT INTO \"Repositories\" (\"Path\") VALUES ('/unique/repo');"));
    }

    [Fact]
    public async Task InitializeEngineAsync_MergesDuplicateRepositoryPaths_BeforeCreatingIndex()
    {
        using var temp = new TempDirectory();
        var dbFile = temp.File("data.db");

        await InitializeDatabaseAsync(temp.Path);

        // Simulate a legacy database: the index is gone and the same path was inserted twice,
        // with run history and a pipeline link attached to each duplicate.
        await ExecuteSqlAsync(
            dbFile,
            "DROP INDEX \"IX_Repositories_Path\";",
            "INSERT INTO \"Repositories\" (\"Path\") VALUES ('/dup/repo');",
            "INSERT INTO \"Repositories\" (\"Path\") VALUES ('/dup/repo');",
            $"INSERT INTO \"{RunTable}\" (\"RepositoryId\", \"PipelineFileName\", \"PipelineName\", \"StartedAt\", \"Status\", \"Trigger\") " +
            "VALUES (1, 'one.yaml', 'one', '2024-01-01 00:00:00+00:00', 0, 'manual');",
            $"INSERT INTO \"{RunTable}\" (\"RepositoryId\", \"PipelineFileName\", \"PipelineName\", \"StartedAt\", \"Status\", \"Trigger\") " +
            "VALUES (2, 'two.yaml', 'two', '2024-01-01 00:00:00+00:00', 0, 'manual');",
            "INSERT INTO \"Pipelines\" (\"Path\", \"Name\") VALUES ('/dup/repo/build.yaml', 'build');",
            "INSERT INTO \"RepositoryPipelines\" (\"RepositoryId\", \"PipelineId\") VALUES (1, 1);",
            "INSERT INTO \"RepositoryPipelines\" (\"RepositoryId\", \"PipelineId\") VALUES (2, 1);");

        Assert.False(await IndexExistsAsync(dbFile, "IX_Repositories_Path"), "Precondition: legacy database must lack the unique index");
        Assert.Equal(2, await CountRowsAsync(dbFile, "SELECT COUNT(*) FROM \"Repositories\" WHERE \"Path\" = '/dup/repo';"));

        // Re-initializing merges the duplicates (keeping the lowest id) and creates the index.
        await InitializeDatabaseAsync(temp.Path);

        Assert.True(await IndexExistsAsync(dbFile, "IX_Repositories_Path"), "Expected the shim to create the unique index");
        Assert.Equal(1, await CountRowsAsync(dbFile, "SELECT COUNT(*) FROM \"Repositories\" WHERE \"Path\" = '/dup/repo';"));

        // History is preserved: both runs survive and point at the surviving repository (id 1).
        var repositoryIds = await ReadRunRepositoryIdsAsync(dbFile);
        Assert.Equal(2, repositoryIds.Count);
        Assert.All(repositoryIds, id => Assert.Equal(1, id));

        // The pipeline link is re-pointed to the surviving repository without duplicating it.
        Assert.Equal(1, await CountRowsAsync(dbFile, "SELECT COUNT(*) FROM \"RepositoryPipelines\";"));
        Assert.Equal(1, await CountRowsAsync(dbFile, "SELECT COUNT(*) FROM \"RepositoryPipelines\" WHERE \"RepositoryId\" = 1 AND \"PipelineId\" = 1;"));
    }

    private static async Task InitializeDatabaseAsync(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddEngine(new EngineOptions { DatabasePath = databasePath });

        using var provider = services.BuildServiceProvider();

        await provider.InitializeEngineAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Dictionary<string, (bool NotNull, string? DefaultValue)>> ReadRunColumnsAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{RunTable}\");";

        var columns = new Dictionary<string, (bool NotNull, string? DefaultValue)>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var name = reader.GetString(1);
            var notNull = reader.GetInt32(3) == 1;
            var defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
            columns[name] = (notNull, defaultValue);
        }

        return columns;
    }

    private static async Task<(string? Trigger, string? TriggerDetail)> ReadRunTriggerAsync(string databaseFile, int runId)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"Trigger\", \"TriggerDetail\" FROM \"{RunTable}\" WHERE \"Id\" = $id;";
        command.Parameters.AddWithValue("$id", runId);

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), $"Expected a Run row with Id {runId}");

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<int> CountRunsAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{RunTable}\";";

        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteSqlAsync(string databaseFile, params string[] statements)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<bool> IndexExistsAsync(string databaseFile, string indexName)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE \"type\" = 'index' AND lower(\"name\") = lower($name);";
        command.Parameters.AddWithValue("$name", indexName);

        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is not null;
    }

    private static async Task<int> CountRowsAsync(string databaseFile, string countSql)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = countSql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<int>> ReadRunRepositoryIdsAsync(string databaseFile)
    {
        await using var connection = new SqliteConnection($"Data Source={databaseFile}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"RepositoryId\" FROM \"{RunTable}\" ORDER BY \"Id\";";

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }
}
