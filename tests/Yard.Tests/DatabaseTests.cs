using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using Yard.DotNet;
using YardApp.Api.Infrastructure;
using YardApp.Api.Modules.Notes;

namespace Yard.Tests;

public sealed class DatabaseTests {
    private static AppDbContext Create(DbCommandInterceptor? interceptor = null) {
        var directory = Path.Combine(
            WorkspaceTests.FindRoot(),
            ".temp",
            "database-unit",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(
            "Data Source=" + Path.Combine(directory, "test.db") + ";Foreign Keys=True"
        );
        if (interceptor != null)
            options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    [Fact]
    public async Task FlushPreservesSchemaAndHistoryAndResetsSequences() {
        await using var db = Create();
        await DatabaseManagement.Run(db, "migrate", default);
        db.Notes.Add(new Note { Id = 99, Text = "Delete me" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE RawParent (Id INTEGER PRIMARY KEY AUTOINCREMENT); CREATE TABLE RawChild (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES RawParent(Id) ON DELETE RESTRICT); INSERT INTO RawParent VALUES (50); INSERT INTO RawChild VALUES (1,50)"
        );
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await DatabaseManagement.Run(db, "flush", default);
        Assert.Empty(await db.Notes.ToArrayAsync());
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
        db.Notes.Add(new Note { Text = "After flush" });
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.Notes.SingleAsync()).Id);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO RawParent DEFAULT VALUES; INSERT INTO RawChild VALUES (1,1)"
        );
    }

    [Fact]
    public async Task FailedFlushRollsBackData() {
        await using var db = Create();
        await db.Database.MigrateAsync();
        db.Notes.Add(new Note { Text = "Keep me" });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE ZFailure (Id INTEGER PRIMARY KEY); INSERT INTO ZFailure VALUES (1); CREATE TRIGGER FailDelete BEFORE DELETE ON ZFailure BEGIN SELECT RAISE(ABORT,'controlled failure'); END"
        );
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseManagement.Run(db, "flush", default));
        Assert.Equal("Keep me", (await db.Notes.SingleAsync()).Text);
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("plan")]
    [InlineData("sql")]
    [InlineData("flush")]
    [InlineData("status")]
    public async Task UnknownHistoryIsRejected(string verb) {
        await using var db = Create();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Foreign','10.0.13')"
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseManagement.Run(db, verb, default)
        );
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("sql")]
    [InlineData("flush")]
    [InlineData("status")]
    public async Task MissingDatabaseIsNotCreated(string verb) {
        await using var db = Create();
        var path = db.Database.GetDbConnection().DataSource;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseManagement.Run(db, verb, default)
        );
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task PreviewLeavesHistoryAndDataUnchanged() {
        await using var db = Create();
        await db.Database.MigrateAsync();
        db.Notes.Add(new Note { Text = "Keep me" });
        await db.SaveChangesAsync();
        var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await DatabaseManagement.Run(db, "plan", default);
        await DatabaseManagement.Run(db, "sql", default);
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
        Assert.Single(await db.Notes.ToArrayAsync());
    }

    [Fact]
    public async Task CancellationAfterDeletionRollsBackFlush() {
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelDeletion(cancellation);
        await using var db = Create(interceptor);
        await db.Database.MigrateAsync();
        db.Notes.Add(new Note { Text = "Survive cancellation" });
        await db.SaveChangesAsync();
        interceptor.Enabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseManagement.Run(db, "flush", cancellation.Token)
        );
        Assert.Equal("Survive cancellation", (await db.Notes.AsNoTracking().SingleAsync()).Text);
    }

    private sealed class CancelDeletion(CancellationTokenSource cancellation) : DbCommandInterceptor {
        public bool Enabled { get; set; }

        public override ValueTask<int> NonQueryExecutedAsync(
            System.Data.Common.DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        ) {
            if (Enabled && command.CommandText.StartsWith("DELETE FROM", StringComparison.Ordinal))
                cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
