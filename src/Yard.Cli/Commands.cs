using System.Net.Http.Json;
using System.Text.Json;
using Yard;

namespace Yard.Cli;

public sealed class Commands(Workspace w, CancellationToken ct) {
    private Dictionary<string, string?> Env(string app) {
        var environment = new Dictionary<string, string?> {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
            ["Logging__LogLevel__Default"] = "Warning",
            ["DOTNET_NOLOGO"] = "1",
        };
        if (File.Exists(LocalSettings.PathFor(w)))
            environment["ConnectionStrings__Default"] = LocalSettings.Load(w).Connections[app];
        return environment;
    }

    private string ToolProject(string app) =>
        Path.Combine(w.State, "tools", app, "Yard.Tools.csproj");

    private string ToolAssembly(string app) =>
        Path.Combine(w.State, "tools", app, "bin", "Debug", "net10.0", "Yard.Tools.dll");

    private async Task BuildTool(string app, bool outputToError = false) {
        if (!File.Exists(ToolProject(app)))
            throw new YardException("Development tools are missing. Run yard install.");
        await Processes.Run(
            "dotnet",
            ["build", ToolProject(app), "--no-restore"],
            w.Root,
            ct,
            outputToError: outputToError
        );
    }

    private void GenerateTool(string app) {
        var directory = Path.GetDirectoryName(ToolProject(app))!;
        Directory.CreateDirectory(directory);
        var reference = System.Security.SecurityElement.Escape(w.Project(app));
        File.WriteAllText(
            ToolProject(app),
            $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include=\"{reference}\"/><PackageReference Include=\"Iugam.Yard.DotNet\" Version=\"{Versions.Yard}\"/></ItemGroup></Project>"
        );
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project/>");
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"), "<Project/>");
        var appNamespace = w.ProjectName(app);
        File.WriteAllText(
            Path.Combine(directory, "Program.cs"),
            $$"""
            using {{appNamespace}}.Hosting;
            using {{appNamespace}}.Infrastructure;
            using Microsoft.Extensions.Logging;
            return await Yard.DotNet.DevelopmentTools.Run(args,
                () => new AppDbContextFactory().CreateDbContext([]),
                () => {
                    var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
                        ApplicationName = typeof(AppDbContext).Assembly.GetName().Name,
                        ContentRootPath = {{JsonSerializer.Serialize(
                Path.GetDirectoryName(w.Project(app))
            )}}
                    });
                    builder.Logging.ClearProviders();
                    builder.AddApplication();
                    var app = builder.Build();
                    app.UseApplication();
                    return app;
                });
            """
                + "\n"
        );
    }

    private Task Run(
        string file,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? env = null,
        string? input = null
    ) => Processes.Run(file, args, w.Root, ct, env, input);

    public async Task Build(string? app = null) {
        foreach (var a in w.Select(app)) {
            if (w.Definition.Backends.Contains(a))
                await Run("dotnet", ["build", w.Project(a), "--no-restore"]);
            else
                await Run("pnpm", ["--dir", w.AppDirectory(a), "run", "build"]);
        }
    }

    public async Task Install() {
        RequireStructure();
        Templates.Feed(w);
        await Run("dotnet", ["tool", "restore", "--add-source", Path.Combine(w.State, "feed")]);
        File.WriteAllText(
            Path.Combine(w.State, "NuGet.Config"),
            new System.Xml.Linq.XElement(
                "configuration",
                new System.Xml.Linq.XElement(
                    "packageSources",
                    new System.Xml.Linq.XElement("clear"),
                    new System.Xml.Linq.XElement(
                        "add",
                        new System.Xml.Linq.XAttribute("key", "yard-local"),
                        new System.Xml.Linq.XAttribute("value", Path.Combine(w.State, "feed"))
                    ),
                    new System.Xml.Linq.XElement(
                        "add",
                        new System.Xml.Linq.XAttribute("key", "nuget.org"),
                        new System.Xml.Linq.XAttribute(
                            "value",
                            "https://api.nuget.org/v3/index.json"
                        )
                    )
                )
            ).ToString()
        );
        var host = Path.Combine(w.State, "host");
        Directory.CreateDirectory(host);
        File.WriteAllText(
            Path.Combine(host, "Yard.Host.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><Sdk Name=\"Aspire.AppHost.Sdk\" Version=\"{Versions.Aspire}\"/><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsAspireHost>true</IsAspireHost><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><PackageReference Include=\"Iugam.Yard.DevHost\" Version=\"{Versions.Yard}\"/></ItemGroup></Project>"
        );
        File.WriteAllText(
            Path.Combine(host, "Program.cs"),
            "await Yard.DevHost.DevelopmentHost.RunAsync(args);\n"
        );
        File.WriteAllText(Path.Combine(host, "Directory.Build.props"), "<Project/>");
        File.WriteAllText(Path.Combine(host, "Directory.Packages.props"), "<Project/>");
        await Run("dotnet", ["restore", w.Definition.Name + ".slnx", "--locked-mode"]);
        await Run("dotnet", ["restore", Path.Combine(host, "Yard.Host.csproj")]);
        await Run("dotnet", ["build", Path.Combine(host, "Yard.Host.csproj"), "--no-restore"]);
        foreach (var app in w.Definition.Backends) {
            GenerateTool(app);
            await Run("dotnet", ["restore", ToolProject(app)]);
            await BuildTool(app);
        }
        await Run("pnpm", ["install", "--frozen-lockfile"]);
    }

    public void RequireStructure() {
        var errors = w.StructureErrors();
        if (errors.Count > 0)
            throw new YardException(string.Join("\n", errors));
    }

    public async Task Initialize() {
        RequireStructure();
        Synchronization.Apply(w, false);
        LocalSettings.Configure(w);
        await Install();
        await Database("up", null, false, null);
        await Database("migrate", null, true, null);
        Console.WriteLine("Initialized. Run yard dev to start development.");
    }

    public async Task Database(
        string verb,
        string? app,
        bool all,
        string? migration,
        bool yes = false,
        string? sqlOutput = null
    ) {
        if (all && app != null)
            throw new YardException("--app and --all cannot be combined.", 2);
        if (all && verb is "shell" or "sql")
            throw new YardException("This database command accepts one backend only.", 2);
        var targets = all ? w.OrderedBackends() : w.Select(app ?? "api", true);
        var s = LocalSettings.Load(w);
        if (verb is "up" or "down") {
            await DatabaseHost(verb, s);
            return;
        }
        if (verb == "shell") {
            await DatabaseShell(targets.Single(), s);
            return;
        }
        if (verb == "flush" && !yes) {
            Console.Error.WriteLine(
                $"Clear ALL data in checkout {w.Root} ({w.Identity}), provider {w.Definition.Database}:"
            );
            foreach (var target in targets)
                Console.Error.WriteLine($"  {target}: {DatabaseLocation(s, target)}");
            if (Console.IsInputRedirected)
                throw new YardException(
                    "Redirected input requires --yes to confirm database flushing.",
                    2
                );
            Console.Error.Write(
                "All application data will be deleted; migration history is retained. Type yes to continue [cancel]: "
            );
            // Console's synchronized reader blocks even in ReadLineAsync; wait separately so Ctrl+C can exit.
            if (await Task.Run(Console.ReadLine, ct).WaitAsync(ct) != "yes")
                throw new OperationCanceledException("Database flush cancelled.", ct);
        }
        var completed = new List<string>();
        try {
            foreach (var target in targets) {
                await BuildTool(target, outputToError: verb == "sql");
                if (verb == "generate") {
                    if (
                        string.IsNullOrWhiteSpace(migration)
                        || !System.Text.RegularExpressions.Regex.IsMatch(
                            migration,
                            "^[A-Za-z][A-Za-z0-9]*$"
                        )
                    )
                        throw new YardException("A C# migration name is required.", 2);
                    await Run(
                        "dotnet",
                        [
                            "ef",
                            "migrations",
                            "add",
                            migration,
                            "--project",
                            w.Project(target),
                            "--context",
                            "AppDbContext",
                            "--output-dir",
                            "Infrastructure/Migrations",
                            "--no-build",
                        ],
                        Env(target)
                    );
                } else if (verb == "sql") {
                    var sql = await Processes.Capture(
                        "dotnet",
                        [ToolAssembly(target), "db", "sql"],
                        w.Root,
                        ct,
                        Env(target)
                    );
                    if (sqlOutput == null)
                        Console.Write(sql);
                    else
                        await File.WriteAllTextAsync(Path.GetFullPath(sqlOutput, w.Root), sql, ct);
                } else if (verb == "plan") {
                    var result = await Processes.Capture(
                        "dotnet",
                        [ToolAssembly(target), "db", "plan"],
                        w.Root,
                        ct,
                        Env(target)
                    );
                    using var document = JsonDocument.Parse(result);
                    var pending = document.RootElement.GetProperty("pending");
                    Console.WriteLine($"Database plan: {target} ({w.Definition.Database})");
                    if (pending.GetArrayLength() == 0)
                        Console.WriteLine("  No pending migrations.");
                    foreach (var item in pending.EnumerateArray())
                        Console.WriteLine("  Apply " + item.GetString());
                } else
                    await Run("dotnet", [ToolAssembly(target), "db", verb], Env(target));
                completed.Add(target);
            }
        }
        catch {
            if (completed.Count > 0)
                Console.Error.WriteLine(
                    "Completed database targets: " + string.Join(", ", completed)
                );
            throw;
        }
    }

    private static string DatabaseLocation(LocalSettings settings, string app) {
        var connection = new System.Data.Common.DbConnectionStringBuilder {
            ConnectionString = settings.Connections[app],
        };
        return Convert.ToString(
            connection[settings.Database == "sqlite" ? "Data Source" : "Database"]
        )!;
    }

    private async Task DatabaseShell(string app, LocalSettings settings) {
        string executable;
        string[] args;
        if (w.Definition.Database == "sqlite") {
            var path = Path.GetFullPath(DatabaseLocation(settings, app), w.Root);
            if (!File.Exists(path))
                throw new YardException("Database file is missing. Run yard init first.");
            executable = "sqlite3";
            args = ["-bail", "-cmd", "PRAGMA foreign_keys=ON;", path];
        } else {
            var container = "yard-" + w.Identity;
            string owner;
            try {
                owner = await Processes.Capture(
                    "docker",
                    [
                        "inspect",
                        "--format",
                        "{{index .Config.Labels \"iugam.yard.checkout\"}} {{.State.Running}}",
                        container,
                    ],
                    w.Root,
                    ct
                );
            }
            catch (YardException) {
                throw new YardException("Database container is unavailable. Run yard db up first.");
            }
            catch (System.ComponentModel.Win32Exception) {
                throw new YardException(
                    "Docker is missing. Install Docker and run yard db up first."
                );
            }
            var identity = owner.Trim().Split(' ');
            if (identity.FirstOrDefault() != w.Identity)
                throw new YardException(
                    "Database container ownership does not match this checkout."
                );
            if (identity.LastOrDefault() != "true")
                throw new YardException("Database container is stopped. Run yard db up first.");
            executable = "docker";
            args =
            [
                "exec",
                "-i",
                .. (
                    !Console.IsInputRedirected && !Console.IsOutputRedirected ? new[] { "-t" } : []
                ),
                container,
                "psql",
                "-X",
                "-v",
                "ON_ERROR_STOP=1",
                "-h",
                "127.0.0.1",
                "-U",
                "yard",
                "-d",
                DatabaseLocation(settings, app),
            ];
        }
        try {
            var exitCode = await Processes.Inherit(
                executable,
                args,
                w.Root,
                ct,
                cancellationTimeout: TimeSpan.FromSeconds(2)
            );
            if (exitCode != 0)
                throw new YardException(
                    "Database client exited with code " + exitCode + ".",
                    exitCode
                );
        }
        catch (System.ComponentModel.Win32Exception) {
            throw new YardException(
                $"Database client '{executable}' is missing. Install {executable} and retry yard db shell."
            );
        }
    }

    public async Task DatabaseHost(string verb, LocalSettings s) {
        if (w.Definition.Database == "sqlite") {
            Console.WriteLine("SQLite uses checkout-local files; no database process is needed.");
            return;
        }
        var name = "yard-" + w.Identity;
        var envFile = Path.Combine(w.State, "postgres.env");
        File.WriteAllText(
            envFile,
            "POSTGRES_USER=yard\nPOSTGRES_PASSWORD=" + s.DatabasePassword + "\n"
        );
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(envFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (verb == "down") {
            var existing = await Processes.Capture(
                "docker",
                ["ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.Names}}"],
                w.Root,
                ct
            );
            if (existing.Trim().Length == 0)
                return;
            var owner = await Processes.Capture(
                "docker",
                ["inspect", "--format", "{{index .Config.Labels \"iugam.yard.checkout\"}}", name],
                w.Root,
                ct
            );
            if (owner.Trim() != w.Identity)
                throw new YardException(
                    "Database container ownership does not match this checkout."
                );
            try {
                await Run("docker", ["stop", name]);
            }
            catch (YardException) {
                var remaining = await Processes.Capture(
                    "docker",
                    ["ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.Names}}"],
                    w.Root,
                    ct
                );
                if (remaining.Trim().Length > 0)
                    throw;
            }
            return;
        }
        var names = await Processes.Capture(
            "docker",
            ["ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.Names}}"],
            w.Root,
            ct
        );
        if (names.Trim().Length == 0)
            await Run(
                "docker",
                [
                    "run",
                    "-d",
                    "--name",
                    name,
                    "--label",
                    "iugam.yard.checkout=" + w.Identity,
                    "--env-file",
                    envFile,
                    "-p",
                    $"127.0.0.1:{s.DatabasePort}:5432",
                    "-v",
                    name + ":/var/lib/postgresql",
                    "postgres:18.6",
                ]
            );
        else {
            var label = await Processes.Capture(
                "docker",
                ["inspect", "--format", "{{index .Config.Labels \"iugam.yard.checkout\"}}", name],
                w.Root,
                ct
            );
            if (label.Trim() != w.Identity)
                throw new YardException(
                    "Database container ownership does not match this checkout."
                );
            await Run("docker", ["start", name]);
        }
        for (var attempt = 0; attempt < 60; attempt++) {
            try {
                await Processes.Capture(
                    "docker",
                    ["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "yard"],
                    w.Root,
                    ct
                );
                break;
            }
            catch (YardException) when (attempt < 59) {
                await Task.Delay(500, ct);
            }
        }
        foreach (var app in w.Definition.Backends) {
            var db = "yard_" + app.Replace('-', '_');
            var exists = await Processes.Capture(
                "docker",
                [
                    "exec",
                    name,
                    "psql",
                    "-h",
                    "127.0.0.1",
                    "-U",
                    "yard",
                    "-d",
                    "postgres",
                    "-tAc",
                    "SELECT 1 FROM pg_database WHERE datname='" + db + "'",
                ],
                w.Root,
                ct
            );
            if (exists.Trim() != "1")
                await Run(
                    "docker",
                    ["exec", name, "createdb", "-h", "127.0.0.1", "-U", "yard", db]
                );
        }
    }

    public async Task AwaitDevelopmentStop() {
        var settings = LocalSettings.Load(w);
        using var control = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        control.DefaultRequestHeaders.Add("X-Yard-Control", settings.ControlToken);
        try {
            using var response = await control.PostAsJsonAsync(
                $"http://127.0.0.1:{settings.ControlPort}/stop",
                new { },
                CancellationToken.None
            );
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        for (var attempt = 0; attempt < 200; attempt++) {
            if (!File.Exists(Path.Combine(w.State, "dev.json")))
                return;
            await Task.Delay(200);
        }
        throw new YardException(
            "The owned development host did not finish cancellation. Inspect yard dev status."
        );
    }

    public async Task Dev(string? action, string? app) {
        var s = LocalSettings.Load(w);
        if (action is "status" or "stop" or "restart") {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Add("X-Yard-Control", s.ControlToken);
            try {
                var response = await http.PostAsJsonAsync(
                    $"http://127.0.0.1:{s.ControlPort}/{action}",
                    new { app },
                    ct
                );
                response.EnsureSuccessStatusCode();
                Console.WriteLine(await response.Content.ReadAsStringAsync(ct));
            }
            catch (HttpRequestException) {
                throw new YardException(
                    "No owned Yard development host is reachable, or the resource command failed. Inspect the foreground Yard log."
                );
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) {
                throw new YardException("The development control command timed out.");
            }
            return;
        }
        RequireStructure();
        await DatabaseHost("up", s);
        var activeApps = w.DevelopmentApps(app);
        var ports = activeApps
            .Select(a => s.Ports[a])
            .Concat([
                s.ControlPort,
                int.Parse(
                    Environment.GetEnvironmentVariable("YARD_PORT") ?? s.GatewayPort.ToString()
                ),
            ]);
        foreach (var port in ports) {
            try {
                using var listener = new System.Net.Sockets.TcpListener(
                    System.Net.IPAddress.Loopback,
                    port
                );
                listener.Start();
            }
            catch (System.Net.Sockets.SocketException) {
                throw new YardException(
                    $"Development port {port} is already in use. Stop its owner or choose YARD_PORT for the gateway."
                );
            }
        }
        var selected = w.DevelopmentApps(app);
        foreach (var backend in selected.Where(w.Definition.Backends.Contains)) {
            await BuildTool(backend);
            var output = await Processes.Capture(
                "dotnet",
                [ToolAssembly(backend), "db", "status"],
                w.Root,
                ct,
                Env(backend)
            );
            using var state = JsonDocument.Parse(output.Trim());
            if (state.RootElement.GetProperty("pending").GetArrayLength() > 0)
                throw new YardException("Pending migrations. Run yard db migrate before yard dev.");
        }
        var env = new Dictionary<string, string?> {
            ["YARD_ROOT"] = w.Root,
            ["YARD_APPS"] = string.Join(',', selected),
            ["DOTNET_USE_POLLING_FILE_WATCHER"] = "1",
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };
        var exitCode = await Processes.Inherit(
            "dotnet",
            [Path.Combine(w.State, "host", "bin", "Debug", "net10.0", "Yard.Host.dll")],
            w.Root,
            ct,
            env,
            AwaitDevelopmentStop
        );
        if (exitCode != 0)
            throw new YardException(
                "Development host exited with code " + exitCode + ".",
                exitCode
            );
    }

    public async Task Format(bool check, string? file) {
        if (file != null) {
            var full = Path.GetFullPath(Path.Combine(w.Root, file));
            if (!full.StartsWith(w.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new YardException("File must belong to this repository.", 2);
            if (file.EndsWith(".cs")) {
                await CSharpFormatting.Run(w, check, file, ct);
                return;
            }
            await Run("pnpm", ["exec", "prettier", check ? "--check" : "--write", file]);
            return;
        }
        await CSharpFormatting.Run(w, check, null, ct);
        await Run(
            "pnpm",
            [
                "exec",
                "prettier",
                check ? "--check" : "--write",
                "apps/**/*.ts",
                "apps/**/*.tsx",
                "!apps/**/bin/**",
                "!apps/**/obj/**",
                "packages/**/*.ts",
                "packages/**/*.tsx",
                "packages/**/*.css",
            ]
        );
    }

    public async Task Engineering(string verb, string? app) {
        RequireStructure();
        if (verb == "check") {
            if (Synchronization.Apply(w, true).Length > 0)
                throw new YardException("Managed files differ. Run yard sync.");
            await Format(true, null);
            await Engineering("lint", app);
            await Engineering("typecheck", app);
            await Contracts(true);
            await Build(app);
            await Engineering("test", app);
            return;
        }
        if (verb == "lint") {
            await Run("pnpm", ["exec", "eslint", "apps", "packages", "--max-warnings", "0"]);
            return;
        }
        foreach (var a in w.Select(app)) {
            if (w.Definition.Backends.Contains(a)) {
                if (verb == "test") {
                    var dir = Path.Combine(w.AppDirectory(a), "tests");
                    foreach (
                        var p in Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
                    )
                        await TestBackend(a, p);
                }
            } else
                await Run("pnpm", ["--dir", w.AppDirectory(a), "run", verb]);
        }
    }

    private async Task TestBackend(string app, string project) {
        var root = Path.Combine(w.Root, ".temp", "yard-tests", Guid.NewGuid().ToString("N"));
        var fixture = new Workspace(root, w.Definition);
        var settings = LocalSettings.Configure(fixture);
        var tools = new Commands(fixture, ct);
        var env = Env(app);
        env["ConnectionStrings__Default"] = settings.Connections[app];
        try {
            await tools.DatabaseHost("up", settings);
            await BuildTool(app);
            await Run("dotnet", [ToolAssembly(app), "db", "migrate"], env);
            await Run("dotnet", ["test", project, "--no-restore"], env);
        }
        finally {
            if (w.Definition.Database == "postgresql") {
                var name = "yard-" + fixture.Identity;
                try {
                    await Processes.Run("docker", ["rm", "-f", name], w.Root);
                    await Processes.Run("docker", ["volume", "rm", name], w.Root);
                }
                catch (YardException) {
                    Console.Error.WriteLine("Isolated test cleanup failed for " + name);
                }
            }
        }
    }

    public async Task Contracts(bool check) {
        // Export from a separate tool process: no HTTP listener or hosted services.
        await BuildTool("api");
        var contractEnv = Env("api");
        contractEnv["ConnectionStrings__Default"] =
            w.Definition.Database == "sqlite"
                ? "Data Source=:memory:"
                : "Host=localhost;Database=yard_design;Username=yard;Password=design-only";
        var json = await Processes.Capture(
            "dotnet",
            [ToolAssembly("api"), "contracts"],
            w.Root,
            ct,
            contractEnv
        );
        using var doc = JsonDocument.Parse(json);
        if (
            !doc.RootElement.TryGetProperty("openapi", out _)
            || (
                doc.RootElement.TryGetProperty("paths", out var paths)
                && paths.ValueKind != JsonValueKind.Object
            )
        )
            throw new YardException("Invalid OpenAPI document.");
        var normalized = JsonSerializer.Serialize(doc.RootElement, Workspace.Json) + "\n";
        var schema = Path.Combine(w.Root, "packages", "api-client", "openapi.json");
        var output = Path.Combine(w.Root, "packages", "api-client", "src", "generated.ts");
        var staging = Path.Combine(w.State, "contracts");
        Directory.CreateDirectory(staging);
        var input = Path.Combine(staging, "openapi.json");
        var generated = Path.Combine(staging, "generated.ts");
        File.WriteAllText(input, normalized);
        await Run("pnpm", ["exec", "openapi-typescript", input, "--output", generated]);
        var ignore = Path.Combine(staging, "empty.ignore");
        File.WriteAllText(ignore, "");
        await Run("pnpm", ["exec", "prettier", "--write", generated, "--ignore-path", ignore]);
        if (check) {
            if (
                !File.Exists(schema)
                || File.ReadAllText(schema) != normalized
                || !File.Exists(output)
                || File.ReadAllText(output) != File.ReadAllText(generated)
            )
                throw new YardException("API contracts are out of date. Run yard contracts.");
        } else {
            File.WriteAllText(schema, normalized);
            File.Copy(generated, output, true);
        }
    }

    public async Task Doctor() {
        RequireStructure();
        if (w.Definition.ToolVersion != Versions.Yard)
            throw new YardException(
                $"Project pins Yard {w.Definition.ToolVersion}, current tool is {Versions.Yard}."
            );
        var changes = Synchronization.Apply(w, true);
        if (changes.Length > 0)
            throw new YardException("Managed configuration differs: " + string.Join(", ", changes));
        var sdk = (await Processes.Capture("dotnet", ["--version"], w.Root, ct)).Trim();
        var node = (await Processes.Capture("node", ["--version"], w.Root, ct)).Trim();
        var pnpm = (await Processes.Capture("pnpm", ["--version"], w.Root, ct)).Trim();
        if (sdk != Versions.Dotnet || node != "v" + Versions.Node || pnpm != Versions.Pnpm)
            throw new YardException(
                $"Expected dotnet {Versions.Dotnet}, Node {Versions.Node}, pnpm {Versions.Pnpm}; found {sdk}, {node}, {pnpm}."
            );
        Console.WriteLine("Yard configuration, applications and toolchain are valid.");
    }

    public async Task Verify() {
        await Doctor();
        await Engineering("check", null);
        var path = Path.Combine(
            w.Root,
            ".temp",
            "verification",
            DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]
        );
        Directory.CreateDirectory(path);
        foreach (var source in Directory.GetFiles(w.Root, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(w.Root, source);
            if (
                relative
                    .Split(Path.DirectorySeparatorChar)
                    .Any(x =>
                        x
                            is ".yard"
                                or ".git"
                                or ".temp"
                                or "node_modules"
                                or "bin"
                                or "obj"
                                or "dist"
                    )
            )
                continue;
            var destination = Path.Combine(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        var fixture = Workspace.Load(path);
        var commands = new Commands(fixture, ct);
        System.Diagnostics.Process? host = null;
        Task<string>? hostOutput = null;
        Task<string>? hostErrors = null;
        try {
            await commands.Initialize();
            var port = LocalSettings.FreePort();
            var url = "http://localhost:" + port;
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") {
                WorkingDirectory = path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("dev");
            start.Environment["YARD_PORT"] = port.ToString();
            start.Environment["YARD_LOCAL_EXECUTION"] = "1";
            host =
                System.Diagnostics.Process.Start(start)
                ?? throw new YardException("Could not start the isolated verification host.");
            var output = hostOutput = host.StandardOutput.ReadToEndAsync();
            var errors = hostErrors = host.StandardError.ReadToEndAsync();
            using var http = new HttpClient();
            var ready = false;
            for (var i = 0; i < 180 && !host.HasExited; i++) {
                try {
                    using var response = await http.GetAsync(url + "/health/ready", ct);
                    if (response.IsSuccessStatusCode) {
                        ready = true;
                        break;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, ct);
            }
            if (!ready)
                throw new YardException(
                    "Verification host failed to become ready. See the retained fixture at " + path
                );
            await Processes.Run("pnpm", ["exec", "playwright", "install", "chromium"], path, ct);
            var env = new Dictionary<string, string?> {
                ["YARD_VERIFY_URL"] = url,
                ["YARD_VERIFY_ISOLATED"] = "1",
            };
            foreach (var project in new[] { "desktop", "mobile" }) {
                if (project == "mobile") {
                    await commands.Dev("restart", "api");
                    for (var attempt = 0; attempt < 120; attempt++) {
                        try {
                            using var readyResponse = await http.GetAsync(
                                url + "/health/ready",
                                ct
                            );
                            if (readyResponse.IsSuccessStatusCode)
                                break;
                        }
                        catch (HttpRequestException) { }
                        if (attempt == 119)
                            throw new YardException("API did not recover after restart.");
                        await Task.Delay(500, ct);
                    }
                }
                await Processes.Run(
                    "pnpm",
                    [
                        "exec",
                        "playwright",
                        "test",
                        "--config",
                        "tests/playwright.config.ts",
                        "--project",
                        project,
                    ],
                    path,
                    ct,
                    env
                );
            }
            var program = Path.Combine(
                Path.GetDirectoryName(fixture.Project("api"))!,
                "Program.cs"
            );
            var originalProgram = File.ReadAllText(program);
            if (!originalProgram.Contains("app.Run();"))
                throw new YardException(
                    "Verification requires the standard app.Run() entry point."
                );
            async Task Probe(string expected) {
                for (var attempt = 0; attempt < 150 && !host.HasExited; attempt++) {
                    try {
                        if (
                            await http.GetStringAsync(url + "/api/__yard/verification-probe", ct)
                            == expected
                        )
                            return;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(300, ct);
                }
                throw new YardException("C# watch did not produce " + expected);
            }
            try {
                var probe = originalProgram.Replace(
                    "app.Run();",
                    "app.MapGet(\"/api/__yard/verification-probe\",()=>\"yard-probe-one\");\napp.MapGet(\"/api/__yard/verification-exit\",()=>{Environment.Exit(73);return \"unexpected\";});\napp.Run();"
                );
                File.WriteAllText(program, probe);
                await Probe("yard-probe-one");
                File.WriteAllText(program, probe + "\ninvalid compile probe;\n");
                await Task.Delay(8000, ct);
                File.WriteAllText(program, probe.Replace("yard-probe-one", "yard-probe-two"));
                await Probe("yard-probe-two");
                try {
                    await http.GetAsync(url + "/api/__yard/verification-exit", ct);
                }
                catch (HttpRequestException) { }
                await host.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
                if (host.ExitCode == 0)
                    throw new YardException(
                        "An unexpected backend exit did not fail the owned process group."
                    );
                var captured = await output + await errors;
                if (!captured.Contains("error CS"))
                    throw new YardException(
                        "The compilation-error recovery probe did not observe a compiler failure."
                    );
                var settings = LocalSettings.Load(fixture);
                foreach (
                    var ownedPort in settings.Ports.Values.Concat([port, settings.ControlPort])
                ) {
                    try {
                        using var socket = new System.Net.Sockets.TcpClient();
                        await socket.ConnectAsync("127.0.0.1", ownedPort, ct);
                        throw new YardException(
                            "A stopped verification resource still owns port " + ownedPort
                        );
                    }
                    catch (System.Net.Sockets.SocketException) { }
                }
                File.WriteAllText(Path.Combine(path, "verification.log"), captured);
            }
            finally {
                File.WriteAllText(program, originalProgram);
            }
            Console.WriteLine("Browser verification passed in isolated fixture: " + path);
        }
        finally {
            if (host is { HasExited: false }) {
                try {
                    await commands.Dev("stop", null);
                    await host.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(20));
                }
                catch {
                    host.Kill(entireProcessTree: true);
                }
            }
            if (fixture.Definition.Database == "postgresql") {
                var name = "yard-" + fixture.Identity;
                try {
                    await Processes.Run("docker", ["rm", "-f", name], path);
                    await Processes.Run("docker", ["volume", "rm", name], path);
                }
                catch (YardException) {
                    Console.Error.WriteLine("Verification database cleanup failed for " + name);
                }
            }
            if (host is { HasExited: false }) {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync();
            }
            if (hostOutput != null && hostErrors != null)
                File.WriteAllText(
                    Path.Combine(path, "verification.log"),
                    await hostOutput + await hostErrors
                );
            host?.Dispose();
        }
    }
}
