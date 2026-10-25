using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Yard.DotNet;

namespace Yard.Tests;

public sealed class ToolBoundaryTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContractsNeverCreateDatabaseStartServicesOrListen(bool empty) {
        var marker = new StartupMarker();
        WebApplication? application = null;
        ICollection<string>? addresses = null;
        var result = await DevelopmentTools.Run(
            ["contracts"],
            () =>
                throw new InvalidOperationException("Contract export tried to create a database."),
            () => {
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddOpenApi();
                builder.Services.AddSingleton<IHostedService>(marker);
                application = builder.Build();
                addresses = application.Urls;
                if (!empty)
                    application.MapGet("/api/example", () => new { value = "example" });
                return application;
            }
        );
        Assert.Equal(0, result);
        Assert.False(marker.Started);
        Assert.NotNull(application);
        Assert.NotNull(addresses);
        Assert.Empty(addresses);
    }

    [Fact]
    public async Task DatabaseOperationsDoNotConstructApplication() {
        var path = Path.Combine(
            WorkspaceTests.FindRoot(),
            ".temp",
            "tool-boundary",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        var result = await DevelopmentTools.Run(
            ["db", "migrate"],
            () =>
                new YardApp.Api.Infrastructure.AppDbContext(
                    new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<YardApp.Api.Infrastructure.AppDbContext>()
                        .UseSqlite("Data Source=" + Path.Combine(path, "test.db"))
                        .Options
                ),
            () =>
                throw new InvalidOperationException("Database tooling constructed the application.")
        );
        Assert.Equal(0, result);
    }

    private sealed class StartupMarker : IHostedService {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
