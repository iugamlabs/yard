using Microsoft.EntityFrameworkCore;
using YardApp.Api.Infrastructure;
using YardApp.Api.Modules.Notes;

namespace YardApp.Api.Hosting;

public static class ApplicationHosting {
    public static WebApplicationBuilder AddApplication(this WebApplicationBuilder builder) {
        builder.Services.AddDbContext<AppDbContext>(o =>
            DatabaseConfiguration.Configure(o, builder.Configuration)
        );
        builder.Services.AddScoped<NoteOperations>();
        builder.Services.AddOpenApi();
        return builder;
    }

    public static WebApplication UseApplication(this WebApplication app) {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
            .ExcludeFromDescription();
        app.MapGet(
                "/health/ready",
                async (AppDbContext db) => {
                    try {
                        return
                            await db.Database.CanConnectAsync()
                            && !(await db.Database.GetPendingMigrationsAsync()).Any()
                            ? Results.Ok(new { status = "ready" })
                            : Results.StatusCode(503);
                    }
                    catch {
                        return Results.StatusCode(503);
                    }
                }
            )
            .ExcludeFromDescription();
        app.MapOpenApi();
        app.MapNotes();
        return app;
    }
}
