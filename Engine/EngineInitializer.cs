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
    }
}
