using Railway.Engine.Helpers;
using Railway.Engine.Infrastructure;

using Microsoft.EntityFrameworkCore;

namespace Railway.Engine;

/// <summary>
/// Default <see cref="IEngineInitializer"/> that creates the Engine database
/// directory and calls <c>EnsureCreatedAsync</c> when
/// <see cref="EngineOptions.InitializeDatabase"/> is enabled.
/// </summary>
public sealed class EngineInitializer : IEngineInitializer
{
    private readonly EngineOptions _options;
    private readonly DbContextOptions<RailwayDbContext> _dbContextOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="EngineInitializer"/> class.
    /// </summary>
    /// <param name="options">The Engine options controlling database initialization.</param>
    /// <param name="dbContextOptions">The configured context options used to open the database.</param>
    public EngineInitializer(EngineOptions options, DbContextOptions<RailwayDbContext> dbContextOptions)
    {
        _options = options;
        _dbContextOptions = dbContextOptions;
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.InitializeDatabase)
        {
            return;
        }

        var dbPath = _options.DatabasePath ?? DatabasePathHelper.Get();

        if (!DatabasePathHelper.CreatePathIfNotExist(dbPath))
        {
            throw new InvalidOperationException($"The path: {dbPath} could not be created");
        }

        await using var ctx = new RailwayDbContext(_dbContextOptions);
        await ctx.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSchemaUpgradesAsync(ctx, cancellationToken);
    }

    /// <summary>
    /// Startup schema shim (no migration framework): <c>EnsureCreatedAsync</c> is a no-op on existing
    /// databases, so databases created before a feature lack its schema. This adds the
    /// <c>Run.Trigger</c>/<c>Run.TriggerDetail</c> columns when missing and repairs duplicate
    /// <c>Repository.Path</c> rows before creating the unique index over that column. It is
    /// idempotent and a no-op on up-to-date databases.
    /// </summary>
    private static async Task EnsureSchemaUpgradesAsync(RailwayDbContext ctx, CancellationToken cancellationToken)
    {
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await EnsureRunProvenanceColumnsAsync(connection, cancellationToken);
        await EnsureRepositoryPathUniqueIndexAsync(connection, cancellationToken);
    }

    /// <summary>
    /// Adds the run-provenance columns to legacy databases. <c>Trigger</c> gets
    /// <c>NOT NULL DEFAULT 'manual'</c> so existing rows are backfilled. A no-op when both exist or
    /// when <c>EnsureCreatedAsync</c> already owns a fresh schema.
    /// </summary>
    private static async Task EnsureRunProvenanceColumnsAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var table = await FindRunTableAsync(connection, cancellationToken);
        if (table is null)
        {
            // EnsureCreated owns schema creation — never mask a missing table with a failed ALTER.
            return;
        }

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1)); // column name
            }
        }

        if (!existingColumns.Contains("Trigger"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"Trigger\" TEXT NOT NULL DEFAULT 'manual';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!existingColumns.Contains("TriggerDetail"))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"TriggerDetail\" TEXT NULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Creates the unique index over <c>Repository.Path</c> when it is missing. Databases written
    /// before the index existed may hold duplicate rows for the same path (the very race the index
    /// guards against), so those are merged first: run history and pipeline links are re-pointed to
    /// the surviving repository before the duplicates are deleted. Without this, <c>CREATE UNIQUE
    /// INDEX</c> would fail and block startup.
    /// </summary>
    private static async Task EnsureRepositoryPathUniqueIndexAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var table = await FindRepositoriesTableAsync(connection, cancellationToken);
        if (table is null)
        {
            return;
        }

        var indexName = "IX_Repositories_Path";
        if (await IndexExistsAsync(connection, indexName, cancellationToken))
        {
            return;
        }

        await RepairDuplicateRepositoryPathsAsync(connection, table, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE UNIQUE INDEX \"{indexName}\" ON \"{table}\" (\"Path\");";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Merges duplicate <c>Repository</c> rows that share a <c>Path</c> into the lowest-id row,
    /// preserving history by re-pointing <c>Runs</c> and <c>RepositoryPipelines</c> before deleting
    /// the duplicates. A no-op when no duplicates exist.
    /// </summary>
    private static async Task RepairDuplicateRepositoryPathsAsync(
        System.Data.Common.DbConnection connection,
        string repositoriesTable,
        CancellationToken cancellationToken)
    {
        if (!await HasDuplicateRepositoryPathsAsync(connection, repositoriesTable, cancellationToken))
        {
            return;
        }

        var runsTable = await FindRunTableAsync(connection, cancellationToken);
        var pipelinesTable = await FindRepositoryPipelinesTableAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (runsTable is not null)
        {
            // Re-point every run to the lowest-id repository with the same path (a no-op for rows
            // already on the keeper). Must precede the delete so the FK cascade cannot drop history.
            // The WHERE clause skips runs that reference a missing repository, which would otherwise
            // be set to NULL and violate the NOT NULL constraint.
            await ExecuteAsync(
                connection,
                transaction,
                $"UPDATE \"{runsTable}\" SET \"RepositoryId\" = (" +
                $"SELECT MIN(k.\"Id\") FROM \"{repositoriesTable}\" k " +
                $"WHERE k.\"Path\" = (SELECT d.\"Path\" FROM \"{repositoriesTable}\" d WHERE d.\"Id\" = \"{runsTable}\".\"RepositoryId\")) " +
                $"WHERE \"RepositoryId\" IN (SELECT \"Id\" FROM \"{repositoriesTable}\");",
                cancellationToken);
        }

        if (pipelinesTable is not null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT OR IGNORE INTO \"{pipelinesTable}\" (\"RepositoryId\", \"PipelineId\") " +
                $"SELECT MIN(k.\"Id\"), p.\"PipelineId\" FROM \"{pipelinesTable}\" p " +
                $"JOIN \"{repositoriesTable}\" r ON r.\"Id\" = p.\"RepositoryId\" " +
                $"JOIN \"{repositoriesTable}\" k ON k.\"Path\" = r.\"Path\" GROUP BY p.\"PipelineId\";",
                cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                $"DELETE FROM \"{pipelinesTable}\" WHERE \"RepositoryId\" <> (" +
                $"SELECT MIN(k.\"Id\") FROM \"{repositoriesTable}\" k " +
                $"WHERE k.\"Path\" = (SELECT d.\"Path\" FROM \"{repositoriesTable}\" d WHERE d.\"Id\" = \"{pipelinesTable}\".\"RepositoryId\"));",
                cancellationToken);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"DELETE FROM \"{repositoriesTable}\" WHERE \"Id\" <> (" +
            $"SELECT MIN(k.\"Id\") FROM \"{repositoriesTable}\" k WHERE k.\"Path\" = \"{repositoriesTable}\".\"Path\");",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> HasDuplicateRepositoryPathsAsync(
        System.Data.Common.DbConnection connection,
        string repositoriesTable,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM \"{repositoriesTable}\" GROUP BY \"Path\" HAVING COUNT(*) > 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> FindRepositoryPipelinesTableAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"name\" FROM sqlite_master WHERE \"type\" = 'table' AND lower(\"name\") = 'repositorypipelines';";

        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<string?> FindRepositoriesTableAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"name\" FROM sqlite_master WHERE \"type\" = 'table' AND lower(\"name\") IN ('repositories', 'repository');";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.Contains("Repositories") ? "Repositories" : names.FirstOrDefault();
    }

    private static async Task<bool> IndexExistsAsync(
        System.Data.Common.DbConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE \"type\" = 'index' AND lower(\"name\") = lower(@name);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken);
    }

    /// <summary>Resolves the physical name of the run table (EF's <c>Runs</c>) or <see langword="null"/> when absent.</summary>
    private static async Task<string?> FindRunTableAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"name\" FROM sqlite_master WHERE \"type\" = 'table' AND lower(\"name\") IN ('runs', 'run');";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names.Contains("Runs") ? "Runs" : names.FirstOrDefault();
    }
}
