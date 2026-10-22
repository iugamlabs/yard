using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yard.AspNetCore;

namespace YardApp.Api.Infrastructure;

public abstract class AppDbContext(DbContextOptions options, OrganizationScope scope)
    : YardDbContext(options, scope);

public sealed class SqliteAppDbContext(
    DbContextOptions<SqliteAppDbContext> options,
    OrganizationScope scope
) : AppDbContext(options, scope);

public sealed class PostgresAppDbContext(
    DbContextOptions<PostgresAppDbContext> options,
    OrganizationScope scope
) : AppDbContext(options, scope);

public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteAppDbContext> {
    public SqliteAppDbContext CreateDbContext(string[] args) {
        var o = new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite("Data Source=:memory:");
        o.UseOpenIddict();
        return new(o.Options, new OrganizationScope());
    }
}

public sealed class PostgresDesignFactory : IDesignTimeDbContextFactory<PostgresAppDbContext> {
    public PostgresAppDbContext CreateDbContext(string[] args) {
        var o = new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql(
            "Host=localhost;Database=yard_design;Username=yard;Password=design-only"
        );
        o.UseOpenIddict();
        return new(o.Options, new OrganizationScope());
    }
}
