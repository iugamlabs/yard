using Microsoft.EntityFrameworkCore;
using Xunit;
using YardApp.Api.Infrastructure;

public class ArchitectureTests {
    [Fact]
    public void SqliteModelMatchesMigrations() {
        using var db = new SqliteDesignFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void PostgresModelMatchesMigrations() {
        using var db = new PostgresDesignFactory().CreateDbContext([]);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
