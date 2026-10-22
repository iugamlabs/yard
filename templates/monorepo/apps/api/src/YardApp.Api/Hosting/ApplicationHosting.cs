using Yard.AspNetCore;
using YardApp.Api.Infrastructure;

namespace YardApp.Api.Hosting;

public static class ApplicationHosting {
    public static WebApplicationBuilder AddApplication(this WebApplicationBuilder builder) {
        if ((Environment.GetEnvironmentVariable("YARD_DATABASE") ?? "__DATABASE__") == "sqlite") {
            builder.AddYard<SqliteAppDbContext>();
            builder.Services.AddScoped<AppDbContext>(p =>
                p.GetRequiredService<SqliteAppDbContext>()
            );
        } else {
            builder.AddYard<PostgresAppDbContext>();
            builder.Services.AddScoped<AppDbContext>(p =>
                p.GetRequiredService<PostgresAppDbContext>()
            );
        }
        return builder;
    }
}
