using Cli.Infrastructure;

using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<RailwayDbContext>()
	.UseSqlite("Data Source=app.db")
	.Options;
	
await using var db = new RailwayDbContext(options);

await db.Database.EnsureCreatedAsync();

db.Builds.Add(new Build
{
    Name = "My Build",
    CreatedAt = DateTime.UtcNow,
    Status = "Running"
});

await db.SaveChangesAsync();

var builds = await db.Builds
    .OrderByDescending(x => x.CreatedAt)
    .ToListAsync();

foreach (var build in builds)
{
    Console.WriteLine($"{build.Id}: {build.Name} - {build.Status}");
}
