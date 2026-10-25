using Microsoft.EntityFrameworkCore;

namespace YardApp.Api.Infrastructure;

public static class DatabaseConfiguration {
    public static void Configure(DbContextOptionsBuilder options, IConfiguration configuration) {
        var connection =
            configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "Configure ConnectionStrings:Default before running the application."
            );
        options.UseSqlite(connection);
    }
}
