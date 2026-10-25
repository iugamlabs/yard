using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using YardApp.Api.Modules.Notes;

namespace YardApp.Api.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options) {
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder model) =>
        model.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext> {
    public AppDbContext CreateDbContext(string[] args) {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseConfiguration.Configure(options, configuration);
        return new(options.Options);
    }
}
