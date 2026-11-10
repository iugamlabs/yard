using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yard;

public sealed record LocalSettings {
    public string Identity { get; init; } = "";
    public string Database { get; init; } = "";
    public int GatewayPort { get; init; } = 8080;
    public int ControlPort { get; init; }
    public string ControlToken { get; init; } = "";
    public Dictionary<string, int> Ports { get; init; } = [];
    public Dictionary<string, string> Connections { get; init; } = [];
    public string DatabasePassword { get; init; } = "";
    public int DatabasePort { get; init; }

    public static string PathFor(Workspace w) => Path.Combine(w.State, "settings.json");

    public static LocalSettings Load(Workspace w) {
        if (!File.Exists(PathFor(w)))
            throw new YardException("Development settings are missing. Run yard init.");
        var settings =
            JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(PathFor(w)), Workspace.Json)
            ?? throw new YardException("Invalid local settings.");
        if (settings.Database.Length > 0 && settings.Database != w.Definition.Database)
            throw new YardException(
                "The database provider changed after initialization. Use a separate checkout/data directory; Yard will not convert existing databases."
            );
        if (settings.Identity != w.Identity)
            throw new YardException(
                "This .yard directory belongs to another checkout. Remove the copied .yard directory before initialization; no resources were touched."
            );
        if (
            settings.GatewayPort is < 1 or > 65535
            || settings.ControlPort is < 1 or > 65535
            || settings.Ports.Values.Any(p => p is < 1 or > 65535)
        )
            throw new YardException("Invalid development port settings.");
        return settings;
    }

    public static int FreePort() {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    public static LocalSettings Configure(Workspace w) {
        var settings = Plan(w);
        Directory.CreateDirectory(w.State);
        Directory.CreateDirectory(System.IO.Path.Combine(w.State, "data"));
        File.WriteAllText(PathFor(w), JsonSerializer.Serialize(settings, Workspace.Json) + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(PathFor(w), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return settings;
    }

    public static LocalSettings Plan(Workspace w) {
        var existing = File.Exists(PathFor(w)) ? Load(w) : null;
        if (existing != null && existing.Identity != w.Identity)
            throw new YardException(
                "This .yard directory belongs to another checkout. Remove the copied .yard directory before initialization."
            );
        var password =
            existing?.DatabasePassword
            ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var dbPort = existing?.DatabasePort ?? FreePort();
        var ports = existing?.Ports ?? new Dictionary<string, int>();
        var connections = existing?.Connections ?? new Dictionary<string, string>();
        foreach (var app in w.Definition.Backends.Concat(w.Definition.Frontends))
            if (!ports.ContainsKey(app))
                ports[app] = FreePort();
        foreach (var app in w.Definition.Backends) {
            connections[app] =
                w.Definition.Database == "sqlite"
                    ? $"Data Source={System.IO.Path.Combine(w.State, "data", app + ".db")};Foreign Keys=True;Default Timeout=30"
                    : $"Host=127.0.0.1;Port={dbPort};Database=yard_{app.Replace('-', '_')};Username=yard;Password={password}";
        }
        var settings = new LocalSettings {
            Identity = w.Identity,
            Database = w.Definition.Database,
            GatewayPort =
                existing?.GatewayPort
                ?? int.Parse(Environment.GetEnvironmentVariable("YARD_PORT") ?? "8080"),
            ControlPort = existing?.ControlPort ?? FreePort(),
            ControlToken =
                existing?.ControlToken
                ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            Ports = ports,
            Connections = connections,
            DatabasePassword = password,
            DatabasePort = dbPort,
        };
        return settings;
    }
}
