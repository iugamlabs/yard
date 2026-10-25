using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yard;
using Yarp.ReverseProxy.Configuration;

namespace Yard.DevHost;

public sealed record ControlBody(string? App);

public static class DevelopmentHost {
    public static async Task RunAsync(string[] args) {
        var w = Workspace.Find(Environment.GetEnvironmentVariable("YARD_ROOT"));
        var settings = LocalSettings.Load(w);
        var gatewayPort = int.Parse(
            Environment.GetEnvironmentVariable("YARD_PORT") ?? settings.GatewayPort.ToString()
        );
        var selected = (
            Environment.GetEnvironmentVariable("YARD_APPS")
            ?? string.Join(',', w.Definition.Backends.Concat(w.Definition.Frontends))
        ).Split(',');
        if (args.Length == 2 && args[0] == "--watch") {
            await Watch(w, args[1]);
            return;
        }
        if (args.Contains("--gateway")) {
            await Gateway(w, settings, selected, gatewayPort);
            return;
        }
        Directory.CreateDirectory(w.State);
        using var ownedLock = new FileStream(
            Path.Combine(w.State, "dev.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            stop.Cancel();
        };
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions {
                Args = args,
                DisableDashboard = true,
                ProjectDirectory = w.Root,
            }
        );
        builder.Services.AddLogging(logging =>
            logging.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None)
        );
        var assembly = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        var backends = new Dictionary<string, IResourceBuilder<ExecutableResource>>();
        foreach (var name in selected.Where(w.Definition.Backends.Contains)) {
            var backend = builder
                .AddExecutable(name, "dotnet", w.Root, assembly, "--watch", name)
                .WithEnvironment("Logging__LogLevel__Default", "Warning")
                .WithEnvironment("ConnectionStrings__Default", settings.Connections[name])
                .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                .WithEnvironment("ASPNETCORE_URLS", "http://127.0.0.1:" + settings.Ports[name])
                .WithHttpEndpoint(
                    port: settings.Ports[name],
                    targetPort: settings.Ports[name],
                    isProxied: false
                )
                .WithHttpHealthCheck("/health/ready");
            backends[name] = backend;
        }
        foreach (var (name, backend) in backends)
            foreach (var dependency in w.Definition.Dependencies.GetValueOrDefault(name, []))
                if (backends.TryGetValue(dependency, out var target)) {
                    backend.WaitFor(target);
                    backend.WithEnvironment(
                        "Services__" + dependency,
                        "http://127.0.0.1:" + settings.Ports[dependency]
                    );
                }
        foreach (var name in selected.Where(w.Definition.Frontends.Contains)) {
            var front = builder
                .AddViteApp(name, w.AppDirectory(name))
                .WithPnpm(install: false)
                .WithEnvironment("YARD_BASE_PATH", name == "web" ? "/" : "/" + name + "/")
                .WithEnvironment("YARD_GATEWAY_PORT", gatewayPort.ToString())
                .WithEndpoint(
                    "http",
                    e => {
                        e.Port = settings.Ports[name];
                        e.TargetPort = settings.Ports[name];
                        e.IsProxied = false;
                    }
                );
            if (backends.TryGetValue("api", out var api))
                front.WaitFor(api);
        }
        var gateway = builder
            .AddExecutable("gateway", "dotnet", w.Root, assembly, "--gateway")
            .WithEnvironment("YARD_ROOT", w.Root)
            .WithEnvironment("YARD_APPS", string.Join(',', selected))
            .WithEnvironment("YARD_PORT", gatewayPort.ToString())
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
        if (backends.TryGetValue("api", out var primary))
            gateway.WaitFor(primary);
        var distributed = builder.Build();
        var notifications = distributed.Services.GetRequiredService<ResourceNotificationService>();
        var commands = distributed.Services.GetRequiredService<ResourceCommandService>();
        var logs = distributed.Services.GetRequiredService<ResourceLoggerService>();
        var streams = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var observed = new HashSet<string>();
        var controls = WebApplication.CreateBuilder();
        controls.Logging.SetMinimumLevel(LogLevel.Warning);
        controls.WebHost.UseUrls($"http://127.0.0.1:{settings.ControlPort}");
        var control = controls.Build();
        control.Use(
            async (h, next) => {
                if (h.Request.Headers["X-Yard-Control"] != settings.ControlToken) {
                    h.Response.StatusCode = 403;
                    return;
                }
                await next();
            }
        );
        control.MapPost(
            "/status",
            () =>
                Results.Ok(
                    new {
                        checkout = w.Identity,
                        applications = builder.Resources.Select(r => new {
                            name = r.Name,
                            state = notifications.TryGetCurrentState(r.Name, out var e)
                                ? e.Snapshot.State?.Text
                                : "starting",
                        }),
                    }
                )
        );
        control.MapPost(
            "/stop",
            () => {
                _ = Task.Run(async () => {
                    await Task.Delay(200);
                    stop.Cancel();
                });
                return Results.Ok(new { stopping = true });
            }
        );
        var restarting = new HashSet<string>();
        control.MapPost(
            "/restart",
            async (ControlBody b) => {
                var apps = b.App == null ? selected : new[] { b.App };
                if (apps.Any(a => !selected.Contains(a)))
                    return Results.BadRequest(
                        new { error = "Application is not running in this host." }
                    );
                foreach (var name in apps) {
                    lock (restarting)
                        restarting.Add(name);
                    try {
                        var result = await commands.ExecuteCommandAsync(
                            name,
                            "resource-restart",
                            stop.Token
                        );
                        if (!result.Success)
                            return Results.Problem(result.Message, statusCode: 503);
                    }
                    finally {
                        _ = Task.Run(async () => {
                            await Task.Delay(3000);
                            lock (restarting)
                                restarting.Remove(name);
                        });
                    }
                }
                return Results.Ok(new { restarted = apps });
            }
        );
        await control.StartAsync(stop.Token);
        File.WriteAllText(
            Path.Combine(w.State, "dev.json"),
            JsonSerializer.Serialize(
                new {
                    pid = Environment.ProcessId,
                    checkout = w.Identity,
                    gateway = "http://localhost:" + gatewayPort,
                    startedAt = DateTimeOffset.UtcNow,
                }
            )
        );
        Console.WriteLine($"Yard: http://localhost:{gatewayPort} — Ctrl+C stops this checkout.");
        var monitor = Task.Run(async () => {
            try {
                await foreach (var e in notifications.WatchAsync(stop.Token)) {
                    if (observed.Add(e.ResourceId))
                        streams.Add(
                            Task.Run(async () => {
                                try {
                                    await foreach (
                                        var batch in logs.WatchAsync(e.ResourceId)
                                            .WithCancellation(stop.Token)
                                    )
                                        foreach (var line in batch)
                                            Console.WriteLine($"[{e.Resource.Name}] {line.Content}");
                                }
                                catch (OperationCanceledException) { }
                            })
                        );
                    var state = e.Snapshot.State?.Text;
                    if (
                        (selected.Contains(e.Resource.Name) || e.Resource.Name == "gateway")
                        && state != null
                        && KnownResourceStates.TerminalStates.Contains(state)
                        && !stop.IsCancellationRequested
                    ) {
                        lock (restarting)
                            if (restarting.Contains(e.Resource.Name))
                                continue;
                        Console.Error.WriteLine(
                            $"Yard resource {e.Resource.Name} {state}; stopping the owned group."
                        );
                        Environment.ExitCode = 1;
                        stop.Cancel();
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        try {
            await distributed.RunAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally {
            stop.Cancel();
            await control.StopAsync(CancellationToken.None);
            await control.DisposeAsync();
            await monitor;
            await Task.WhenAll(streams);
            await distributed.DisposeAsync();
            var ownedPorts = selected
                .Select(name => settings.Ports[name])
                .Concat([gatewayPort, settings.ControlPort])
                .Distinct()
                .ToArray();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true) {
                var listening = new List<int>();
                foreach (var ownedPort in ownedPorts) {
                    using var client = new System.Net.Sockets.TcpClient();
                    try {
                        await client
                            .ConnectAsync("127.0.0.1", ownedPort)
                            .WaitAsync(TimeSpan.FromMilliseconds(300));
                        listening.Add(ownedPort);
                    }
                    catch (System.Net.Sockets.SocketException) { }
                    catch (TimeoutException) { }
                }
                if (listening.Count == 0)
                    break;
                if (DateTime.UtcNow >= deadline)
                    throw new YardException(
                        "Owned development listeners did not stop: " + string.Join(", ", listening)
                    );
                await Task.Delay(200);
            }
            File.Delete(Path.Combine(w.State, "dev.json"));
        }
    }

    private static async Task Watch(Workspace w, string name) {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => {
            e.Cancel = true;
            stop.Cancel();
        };
        using var term = OperatingSystem.IsWindows()
            ? null
            : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM,
                c => {
                    c.Cancel = true;
                    stop.Cancel();
                }
            );
        var crashed = false;
        var compilationFailed = false;
        void Observe(string line) {
            if (line.Contains("error CS") || line.Contains("Build failed"))
                compilationFailed = true;
            if (line.Contains("Exited with error code") && !stop.IsCancellationRequested) {
                crashed = true;
                stop.Cancel();
                return;
            }
            if (
                line.Contains("Waiting for changes")
                || line.Contains("changes applied")
                || line.Contains("Build succeeded")
            )
                compilationFailed = false;
            if (
                line.Contains("Waiting for a file to change before restarting")
                && !compilationFailed
                && !stop.IsCancellationRequested
            ) {
                crashed = true;
                stop.Cancel();
            }
        }
        try {
            await Processes.Run(
                "dotnet",
                [
                    "watch",
                    "--project",
                    w.Project(name),
                    "--non-interactive",
                    "--no-restore",
                    "run",
                    "--no-launch-profile",
                ],
                w.Root,
                stop.Token,
                new Dictionary<string, string?> {
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                    ["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "1",
                    ["DOTNET_USE_POLLING_FILE_WATCHER"] = "1",
                    ["DOTNET_WATCH_SUPPRESS_STATIC_FILE_HANDLING"] = "1",
                },
                observe: Observe
            );
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (crashed) {
            Console.Error.WriteLine($"Yard backend {name} exited unexpectedly.");
            Environment.ExitCode = 1;
        }
    }

    private static async Task Gateway(
        Workspace w,
        LocalSettings settings,
        string[] selected,
        int port
    ) {
        var b = WebApplication.CreateBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        foreach (var name in selected) {
            if (w.Definition.Backends.Contains(name) && name != "api")
                continue;
            clusters.Add(
                new ClusterConfig {
                    ClusterId = name,
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        {
                            "local",
                            new() { Address = $"http://127.0.0.1:{settings.Ports[name]}/" }
                        },
                    },
                }
            );
            if (name == "api") {
                routes.Add(
                    new() {
                        RouteId = "api",
                        ClusterId = name,
                        Order = 0,
                        Match = new() { Path = "/api/{**rest}" },
                    }
                );
                routes.Add(
                    new() {
                        RouteId = "health",
                        ClusterId = name,
                        Order = 0,
                        Match = new() { Path = "/health/{**rest}" },
                    }
                );
            } else
                routes.Add(
                    new() {
                        RouteId = name,
                        ClusterId = name,
                        Order = name == "web" ? 100 : 1,
                        Match = new() {
                            Path = name == "web" ? "/{**rest}" : "/" + name + "/{**rest}",
                        },
                    }
                );
        }
        b.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
        var app = b.Build();
        if (!selected.Contains("admin"))
            app.Map("/admin/{**rest}", () => Results.NotFound());
        app.MapReverseProxy();
        await app.RunAsync();
    }
}
