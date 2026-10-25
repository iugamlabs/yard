using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace Yard.AspNetCore;

internal static class DatabaseManagement {
    private sealed record MigrationState(string[] Known, string[] Applied) {
        public string[] Pending => Known.Skip(Applied.Length).ToArray();
    }

    public static async Task Run(YardDbContext db, string verb, CancellationToken ct) {
        if (verb == "status") {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { applied, pending }));
            return;
        }
        if (verb == "check") {
            CheckModel(db);
            Console.WriteLine("Migration model is current.");
            return;
        }
        if (verb is not ("migrate" or "plan" or "sql" or "flush"))
            throw new YardHttpException(400, "unknown_database_command");

        if (verb != "migrate")
            await RequireDatabase(db, ct);
        var state = await ReadState(db, ct);
        if (verb == "migrate") {
            await db.Database.MigrateAsync(ct);
            Console.WriteLine("Database migrations applied.");
        } else if (verb == "plan") {
            Console.WriteLine(
                JsonSerializer.Serialize(new { applied = state.Applied, pending = state.Pending })
            );
        } else if (verb == "sql") {
            Console.WriteLine(
                state.Pending.Length == 0
                    ? "-- No pending migrations."
                    : db.GetService<IMigrator>()
                        .GenerateScript(state.Applied.LastOrDefault() ?? "0", state.Known.Last())
            );
        } else {
            await Flush(db, ct);
            Console.WriteLine(
                "Database data cleared. Create an administrator with yard user bootstrap owner."
            );
        }
    }

    private static void CheckModel(YardDbContext db) {
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException("The model differs from committed migrations.");
    }

    private static async Task<MigrationState> ReadState(YardDbContext db, CancellationToken ct) {
        var known = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        if (applied.Any(x => !known.Contains(x)))
            throw new InvalidOperationException(
                "Unknown migration history; this database does not belong to the current migration chain."
            );
        if (!applied.SequenceEqual(known.Take(applied.Length)))
            throw new InvalidOperationException(
                "Non-contiguous migration history; restore the current migration chain before continuing."
            );
        if (known.Length == 0)
            throw new InvalidOperationException(
                "No committed migrations. Run yard db generate <Name> first."
            );
        CheckModel(db);
        return new(known, applied);
    }

    private static async Task RequireDatabase(YardDbContext db, CancellationToken ct) {
        if (db.Database.IsSqlite()) {
            var connection = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
            if (connection.Mode != SqliteOpenMode.Memory && connection.DataSource != ":memory:") {
                if (!File.Exists(db.Database.GetDbConnection().DataSource))
                    throw new InvalidOperationException(
                        "Database file is missing. Run yard init first."
                    );
                connection.Mode = SqliteOpenMode.ReadWrite;
                db.Database.SetConnectionString(connection.ToString());
            }
        }
        if (!await db.Database.CanConnectAsync(ct))
            throw new InvalidOperationException(
                "Database is unavailable. Run yard db up or yard init first."
            );
    }

    private static async Task Flush(YardDbContext db, CancellationToken ct) {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Recheck while holding the transaction; seed restoration and deletion must commit together.
        var state = await ReadState(db, ct);
        if (state.Pending.Length != 0)
            throw new InvalidOperationException(
                "Pending migrations must be applied with yard db migrate before flushing."
            );
        var options = db.GetService<IDbContextOptions>()
            .Extensions.OfType<RelationalOptionsExtension>()
            .Single();
        var historyName = options.MigrationsHistoryTableName ?? "__EFMigrationsHistory";
        var historySchema =
            options.MigrationsHistoryTableSchema ?? (db.Database.IsSqlite() ? null : "public");
        var quote = db.GetService<ISqlGenerationHelper>();
        var tables = new List<(string Name, string? Schema)>();
        await using (var command = db.Database.GetDbConnection().CreateCommand()) {
            command.Transaction = tx.GetDbTransaction();
            command.CommandText = db.Database.IsSqlite()
                ? "SELECT name, NULL FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name"
                : "SELECT c.relname, n.nspname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname <> 'information_schema' AND n.nspname !~ '^pg_' AND NOT c.relispartition AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype='e') ORDER BY n.nspname,c.relname";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var table = reader.GetString(0);
                var schema = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (
                    (table == historyName && schema == historySchema)
                    || table == "__EFMigrationsLock"
                )
                    continue;
                tables.Add((table, schema));
            }
        }
        if (db.Database.IsSqlite()) {
            await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys=ON", ct);
            foreach (var table in tables) {
                // Identifiers come from the catalog and are delimited by the provider, never interpolated as values.
                var statement = "DELETE FROM " + quote.DelimitIdentifier(table.Name);
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            }
            await using var sequence = db.Database.GetDbConnection().CreateCommand();
            sequence.Transaction = tx.GetDbTransaction();
            sequence.CommandText =
                "SELECT COUNT(*) FROM sqlite_schema WHERE name='sqlite_sequence' AND type='table'";
            if (Convert.ToInt64(await sequence.ExecuteScalarAsync(ct)) != 0)
                foreach (var table in tables)
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"DELETE FROM sqlite_sequence WHERE name={table.Name}",
                        ct
                    );
        } else if (tables.Count > 0) {
            var statement =
                "TRUNCATE TABLE "
                + string.Join(", ", tables.Select(x => quote.DelimitIdentifier(x.Name, x.Schema)))
                + " RESTART IDENTITY";
            await db.Database.ExecuteSqlRawAsync(statement, ct);
        }
        db.ChangeTracker.Clear();
        db.PlatformSettings.Add(new PlatformSettings());
        db.IdentityLocks.Add(new IdentityLock());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
