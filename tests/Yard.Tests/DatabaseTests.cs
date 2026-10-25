using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Yard.AspNetCore;
using YardApp.Api.Infrastructure;

namespace YardApp.Api.Infrastructure {
    public sealed class SqliteAppDbContext(
        DbContextOptions<SqliteAppDbContext> options,
        OrganizationScope scope
    ) : YardDbContext(options, scope);

    public sealed class PostgresAppDbContext(
        DbContextOptions<PostgresAppDbContext> options,
        OrganizationScope scope
    ) : YardDbContext(options, scope);
}

namespace Yard.Tests {
    [DbContext(typeof(SqliteAppDbContext))]
    [Migration("20261023000000_RawTables")]
    public sealed class RawTablesMigration : Migration {
        protected override void Up(MigrationBuilder builder) {
            builder.Sql("CREATE TABLE \"Raw \"\"parent\" (Id INTEGER PRIMARY KEY AUTOINCREMENT)");
            builder.Sql(
                "CREATE TABLE RawChild (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES \"Raw \"\"parent\"(Id) ON DELETE RESTRICT)"
            );
        }
    }

    public sealed class DatabaseTests {
        [Fact]
        public async Task FlushClearsAllTablesAndRestoresBootstrapWithoutChangingSchemaOrHistory() {
            await using var f = await DatabaseFixture.Create();
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            var identity = scope.ServiceProvider.GetRequiredService<IdentityOperations>();
            var user = await identity.Create("owner", "Password-for-tests-123", true);
            var org = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Organizations (Id,Name,OwnerId,Archived,Version) VALUES ({org},'Test',{user.Id},0,0)"
            );
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO OrganizationMembers (OrganizationId,UserId) VALUES ({org},{user.Id})"
            );
            identity.Audit("test", "flush");
            var settings = await db.PlatformSettings.SingleAsync();
            settings.BootstrapCompleted = true;
            settings.RegistrationEnabled = false;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Raw \"\"parent\" (Id) VALUES (99); INSERT INTO RawChild VALUES (1,99)"
            );
            var before = await Schema(db);
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();

            Assert.Equal(0, await ManagementCommands.Run(f.App.Services, ["db", "flush"]));

            db.ChangeTracker.Clear();
            Assert.False(await db.Users.AnyAsync());
            Assert.False(await db.Organizations.AnyAsync());
            Assert.False(await db.OrganizationMembers.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.IdentityAudits.AnyAsync());
            Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM RawChild"));
            Assert.Equal(0L, await Scalar(db, "SELECT COUNT(*) FROM \"Raw \"\"parent\""));
            Assert.Equal(before, await Schema(db));
            Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
            Assert.True((await db.PlatformSettings.SingleAsync()).RegistrationEnabled);
            Assert.False((await db.PlatformSettings.SingleAsync()).BootstrapCompleted);
            Assert.Equal(0, (await db.IdentityLocks.SingleAsync()).Version);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Raw \"\"parent\" DEFAULT VALUES");
            Assert.Equal(1L, await Scalar(db, "SELECT Id FROM \"Raw \"\"parent\""));
            Assert.NotNull(await identity.Create("owner", "Password-for-tests-123", true));
            Assert.Equal(0, await ManagementCommands.Run(f.App.Services, ["db", "flush"]));
        }

        [Fact]
        public async Task FailedSeedRestorationRollsBackDeletionAndSequences() {
            await using var f = await DatabaseFixture.Create();
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Raw \"\"parent\" (Id) VALUES (99); CREATE TRIGGER FailSeed BEFORE INSERT ON PlatformSettings BEGIN SELECT RAISE(ABORT,'controlled seed failure'); END"
            );
            Assert.Equal(1, await ManagementCommands.Run(f.App.Services, ["db", "flush"]));
            Assert.Equal(99L, await Scalar(db, "SELECT Id FROM \"Raw \"\"parent\""));
            Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM PlatformSettings"));
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Raw \"\"parent\" DEFAULT VALUES");
            Assert.Equal(100L, await Scalar(db, "SELECT MAX(Id) FROM \"Raw \"\"parent\""));
        }

        [Theory]
        [InlineData("plan")]
        [InlineData("sql")]
        [InlineData("flush")]
        [InlineData("migrate")]
        public async Task UnknownAndNonContiguousHistoryAreRejected(string verb) {
            await using var f = await DatabaseFixture.Create();
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO __EFMigrationsHistory VALUES ('20990101000000_Foreign','10.0.13')"
            );
            Assert.Equal(1, await ManagementCommands.Run(f.App.Services, ["db", verb]));
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM __EFMigrationsHistory WHERE MigrationId='20990101000000_Foreign'; DELETE FROM __EFMigrationsHistory WHERE MigrationId='20261022081133_InitialCreate'"
            );
            Assert.Equal(1, await ManagementCommands.Run(f.App.Services, ["db", verb]));
        }

        [Theory]
        [InlineData("plan")]
        [InlineData("sql")]
        public async Task PreviewDoesNotChangeDatabaseAtAnyMigrationStage(string verb) {
            await using var f = await DatabaseFixture.Create(migrate: false);
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            await db.Database.OpenConnectionAsync();
            var before = await Schema(db);
            Assert.Equal(0, await ManagementCommands.Run(f.App.Services, ["db", verb]));
            Assert.Equal(before, await Schema(db));
            await db.GetService<IMigrator>().MigrateAsync("20261022081133_InitialCreate");
            var history = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            before = await Schema(db);
            Assert.Equal(0, await ManagementCommands.Run(f.App.Services, ["db", verb]));
            Assert.Equal(before, await Schema(db));
            Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM PlatformSettings"));
            await db.Database.MigrateAsync();
            Assert.Equal(0, await ManagementCommands.Run(f.App.Services, ["db", verb]));
        }

        [Fact]
        public async Task PendingMigrationsPreventFlush() {
            await using var f = await DatabaseFixture.Create(migrate: false);
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            await db.GetService<IMigrator>().MigrateAsync("20261022081133_InitialCreate");
            Assert.Equal(1, await ManagementCommands.Run(f.App.Services, ["db", "flush"]));
            Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM PlatformSettings"));
        }

        [Theory]
        [InlineData("plan")]
        [InlineData("sql")]
        [InlineData("flush")]
        public async Task MissingFileIsNotCreated(string verb) {
            await using var f = await DatabaseFixture.Create(migrate: false);
            Assert.Equal(1, await ManagementCommands.Run(f.App.Services, ["db", verb]));
            Assert.False(File.Exists(Path.Combine(f.Root, "test.db")));
        }

        [Fact]
        public async Task CancelledFlushDoesNotClearData() {
            await using var f = await DatabaseFixture.Create();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ManagementCommands.Run(f.App.Services, ["db", "flush"], cancellation.Token)
            );
            await using var scope = f.App.Services.CreateAsyncScope();
            Assert.True(
                await scope
                    .ServiceProvider.GetRequiredService<YardDbContext>()
                    .PlatformSettings.AnyAsync()
            );
        }

        [Fact]
        public async Task CancellationAfterDeletionRollsBackTheTransaction() {
            using var cancellation = new CancellationTokenSource();
            var interceptor = new CancelDeletion(cancellation);
            await using var f = await DatabaseFixture.Create(interceptor: interceptor);
            await using var scope = f.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"Raw \"\"parent\" (Id) VALUES (99)");
            interceptor.Enabled = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ManagementCommands.Run(f.App.Services, ["db", "flush"], cancellation.Token)
            );
            Assert.Equal(99L, await Scalar(db, "SELECT Id FROM \"Raw \"\"parent\""));
            Assert.Equal(1L, await Scalar(db, "SELECT COUNT(*) FROM PlatformSettings"));
        }

        private static async Task<long> Scalar(YardDbContext db, string sql) {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        private static async Task<string[]> Schema(YardDbContext db) {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_schema ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<string>();
            while (await reader.ReadAsync())
                result.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            return result.ToArray();
        }
    }

    internal sealed class CancelDeletion(CancellationTokenSource cancellation)
        : DbCommandInterceptor {
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

    internal sealed class DatabaseFixture : IAsyncDisposable {
        public WebApplication App { get; private set; } = null!;
        public string Root { get; } =
            Path.Combine(
                WorkspaceTests.FindRoot(),
                ".temp",
                "database-unit",
                Guid.NewGuid().ToString("N")
            );

        public static async Task<DatabaseFixture> Create(
            bool migrate = true,
            DbCommandInterceptor? interceptor = null
        ) {
            var fixture = new DatabaseFixture();
            Directory.CreateDirectory(fixture.Root);
            var builder = WebApplication.CreateBuilder(
                new WebApplicationOptions {
                    EnvironmentName = "Development",
                    ContentRootPath = fixture.Root,
                }
            );
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> {
                    ["Yard:Database"] = "sqlite",
                    ["Yard:ConnectionString"] =
                        "Data Source="
                        + Path.Combine(fixture.Root, "test.db")
                        + ";Foreign Keys=True",
                    ["Yard:VerificationKey"] = Convert.ToBase64String(new byte[32]),
                    ["Yard:PublicOrigin"] = "http://localhost",
                }
            );
            builder.AddYard<SqliteAppDbContext>();
            if (interceptor != null)
                builder.Services.ConfigureDbContext<SqliteAppDbContext>(options =>
                    options.AddInterceptors(interceptor)
                );
            fixture.App = builder.Build();
            if (migrate) {
                await using var scope = fixture.App.Services.CreateAsyncScope();
                await scope
                    .ServiceProvider.GetRequiredService<YardDbContext>()
                    .Database.MigrateAsync();
            }
            return fixture;
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
