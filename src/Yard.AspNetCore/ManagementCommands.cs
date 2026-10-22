using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Yard.AspNetCore;

public static class ManagementCommands {
    public static async Task<int> Run(IServiceProvider services, string[] args) {
        try {
            if (args.Length == 0)
                throw new YardHttpException(400, "management_command_required");
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<YardDbContext>();
            if (args[0] == "contracts") {
                var document = await scope
                    .ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1")
                    .GetOpenApiDocumentAsync();
                await document.SerializeAsync(
                    Console.OpenStandardOutput(),
                    OpenApiSpecVersion.OpenApi3_0,
                    "json",
                    CancellationToken.None
                );
                return 0;
            }
            if (args[0] == "db") {
                if (args.Length < 2)
                    throw new YardHttpException(400, "db_command_required");
                if (args[1] == "migrate") {
                    var known = db.Database.GetMigrations().ToHashSet();
                    if (
                        (await db.Database.GetAppliedMigrationsAsync()).Any(x => !known.Contains(x))
                    )
                        throw new InvalidOperationException(
                            "Unknown migration history; this database does not belong to the current migration chain."
                        );
                    await db.Database.MigrateAsync();
                    Console.WriteLine("Database migrations applied.");
                } else if (args[1] == "status") {
                    var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
                    var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(new { applied, pending }));
                } else if (args[1] == "check") {
                    if (db.Database.HasPendingModelChanges())
                        throw new InvalidOperationException(
                            "The model differs from committed migrations."
                        );
                    Console.WriteLine("Migration model is current.");
                } else
                    throw new YardHttpException(400, "unknown_database_command");
                return 0;
            }
            if (args[0] != "user" || args.Length < 2)
                throw new YardHttpException(400, "unknown_management_command");
            var settings = scope.ServiceProvider.GetRequiredService<YardSettings>();
            if (!settings.Primary)
                throw new YardHttpException(400, "identity_is_owned_by_api");
            var i = scope.ServiceProvider.GetRequiredService<IdentityOperations>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<YardUser>>();
            if (args[1] == "list") {
                foreach (var user in await db.Users.OrderBy(x => x.UserName).ToListAsync())
                    Console.WriteLine(JsonSerializer.Serialize(await i.View(user)));
                return 0;
            }
            if (args.Length < 3)
                throw new YardHttpException(400, "username_required");
            await using var tx = await db.Database.BeginTransactionAsync();
            await i.Lock();
            var verb = args[1];
            var name = args[2];
            YardUser u;
            if (verb is "bootstrap" or "create") {
                if (
                    verb == "bootstrap"
                    && (
                        await db.PlatformSettings.AnyAsync(x => x.BootstrapCompleted)
                        || await db.Users.AnyAsync(x => x.IsPlatformAdministrator)
                    )
                )
                    throw new YardHttpException(409, "bootstrap_already_completed");
                var password = await Console.In.ReadLineAsync() ?? "";
                u = await i.Create(name, password, verb == "bootstrap", verb != "bootstrap");
                if (verb == "bootstrap")
                    (await db.PlatformSettings.SingleAsync()).BootstrapCompleted = true;
            } else {
                u =
                    await users.FindByNameAsync(name)
                    ?? throw new YardHttpException(404, "user_not_found");
                switch (verb) {
                    case "enable":
                        u.IsEnabled = true;
                        IdentityOperations.Check(await users.UpdateAsync(u));
                        break;
                    case "disable":
                        await i.ProtectLastAdmin(u);
                        u.IsEnabled = false;
                        IdentityOperations.Check(await users.UpdateAsync(u));
                        break;
                    case "reset-password":
                        var password = await Console.In.ReadLineAsync() ?? "";
                        IdentityOperations.Password(password);
                        IdentityOperations.Check(
                            await users.ResetPasswordAsync(
                                u,
                                await users.GeneratePasswordResetTokenAsync(u),
                                password
                            )
                        );
                        u.MustChangePassword = true;
                        IdentityOperations.Check(await users.UpdateAsync(u));
                        break;
                    case "revoke-sessions":
                        break;
                    default:
                        throw new YardHttpException(400, "unknown_user_command");
                }
                await i.Revoke(u);
            }
            i.Audit("cli.user." + verb, u.Id.ToString());
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            Console.WriteLine("User command completed.");
            return 0;
        }
        catch (Exception e)
            when (e
                    is YardHttpException
                        or InvalidOperationException
                        or DbUpdateException
                        or System.Data.Common.DbException
            ) {
            Console.Error.WriteLine(
                e is DbUpdateException or System.Data.Common.DbException
                    ? "The account operation conflicts with existing data."
                    : e.Message
            );
            return 1;
        }
    }
}
