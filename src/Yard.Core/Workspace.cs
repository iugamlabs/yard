using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Yard;

public sealed record WorkspaceDefinition {
    public int SchemaVersion { get; init; } = 2;
    public string Name { get; init; } = "";
    public string ToolVersion { get; init; } = Versions.Yard;
    public string Database { get; init; } = "postgresql";
    public string[] Backends { get; init; } = ["api"];
    public string[] Frontends { get; init; } = ["web"];
    public Dictionary<string, string[]> Dependencies { get; init; } = [];
}

public static class Versions {
    public const string Yard = "0.2.0";
    public const string Dotnet = "10.0.400";
    public const string Ef = "10.0.13";
    public const string Aspire = "13.6.3";
    public const string Pnpm = "12.11.2";
    public const string Node = "24.18.0";
}

public sealed class YardException(string message, int exitCode = 1) : Exception(message) {
    public int ExitCode { get; } = exitCode;
}

public sealed class Workspace(string root, WorkspaceDefinition definition) {
    public static readonly JsonSerializerOptions Json = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public string Root { get; } = Path.GetFullPath(root);
    public WorkspaceDefinition Definition { get; } = definition;
    public string State => Path.Combine(Root, ".yard");
    public string Identity =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Root)))[..12];

    public string AppDirectory(string app) => Path.Combine(Root, "apps", app);

    public string ProjectName(string app) =>
        Definition.Name + (app == "api" ? "" : "." + Pascal(app)) + ".Api";

    public string Project(string app) =>
        Path.Combine(AppDirectory(app), "src", ProjectName(app), ProjectName(app) + ".csproj");

    public string Assembly(string app) =>
        Path.Combine(
            AppDirectory(app),
            "src",
            ProjectName(app),
            "bin",
            "Debug",
            "net10.0",
            ProjectName(app) + ".dll"
        );

    public static string Pascal(string text) =>
        string.Concat(text.Split('-').Select(x => char.ToUpperInvariant(x[0]) + x[1..]));

    public static Workspace Find(string? from = null) =>
        TryFind(from)
        ?? throw new YardException(
            "No yard.json found. Run yard new <Name>, or yard init in a standard repository."
        );

    public static Workspace? TryFind(string? from = null) {
        var directory = new DirectoryInfo(Path.GetFullPath(from ?? Environment.CurrentDirectory));
        while (directory != null) {
            if (File.Exists(Path.Combine(directory.FullName, "yard.json")))
                return Load(directory.FullName);
            directory = directory.Parent;
        }
        return null;
    }

    public static Workspace Load(string root) {
        WorkspaceDefinition d;
        try {
            d =
                JsonSerializer.Deserialize<WorkspaceDefinition>(
                    File.ReadAllText(Path.Combine(root, "yard.json")),
                    Json
                ) ?? throw new JsonException();
        }
        catch (JsonException e) {
            throw new YardException("Invalid yard.json: " + e.Message, 2);
        }
        var w = new Workspace(root, d);
        w.Validate();
        return w;
    }

    public void Validate() {
        if (
            Definition.Name == null
            || Definition.Backends == null
            || Definition.Frontends == null
            || Definition.Dependencies == null
            || Definition.ToolVersion == null
            || Definition.Backends.Any(x => x == null)
            || Definition.Frontends.Any(x => x == null)
            || Definition.Dependencies.Any(x => x.Value == null || x.Value.Any(d => d == null))
        )
            throw new YardException(
                "yard.json fields and application identifiers cannot be null.",
                2
            );
        if (Definition.SchemaVersion != 2)
            throw new YardException(
                $"Unsupported schema {Definition.SchemaVersion}; Yard 0.2 requires schema 2; automatic upgrades from the SaaS template are not supported. No files or databases were changed.",
                2
            );
        if (!Regex.IsMatch(Definition.Name, "^[A-Za-z][A-Za-z0-9]*(\\.[A-Za-z][A-Za-z0-9]*)*$"))
            throw new YardException("Project name must be a valid dotted C# namespace.", 2);
        if (Definition.Database is not ("postgresql" or "sqlite"))
            throw new YardException("Database must be postgresql or sqlite.", 2);
        if (!Definition.Backends.Contains("api") || !Definition.Frontends.Contains("web"))
            throw new YardException("The primary api and web applications are required.", 2);
        var all = Definition.Backends.Concat(Definition.Frontends).ToArray();
        if (
            all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length
            || all.Any(x => !Regex.IsMatch(x, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$"))
        )
            throw new YardException(
                "Application names must be unique lowercase kebab-case identifiers.",
                2
            );
        if (
            all.Any(x => x is "gateway" or "health")
            || Definition.Frontends.Any(f => all.Contains(f + "-installer"))
        )
            throw new YardException(
                "An application name collides with a reserved gateway, health route or generated installer resource.",
                2
            );
        foreach (var (app, deps) in Definition.Dependencies)
            if (!all.Contains(app) || deps.Any(x => !Definition.Backends.Contains(x) || x == app))
                throw new YardException("Dependencies must reference other declared backends.", 2);
        _ = OrderedBackends();
    }

    public string[] OrderedBackends() {
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();
        var result = new List<string>();
        void Visit(string app) {
            if (done.Contains(app))
                return;
            if (!visiting.Add(app))
                throw new YardException("Application dependencies contain a cycle.", 2);
            foreach (var dep in Definition.Dependencies.GetValueOrDefault(app, []))
                Visit(dep);
            visiting.Remove(app);
            done.Add(app);
            if (Definition.Backends.Contains(app))
                result.Add(app);
        }
        foreach (var app in Definition.Backends)
            Visit(app);
        return result.ToArray();
    }

    public string[] Select(string? app, bool backendsOnly = false) {
        var all = backendsOnly
            ? Definition.Backends
            : Definition.Backends.Concat(Definition.Frontends).ToArray();
        if (app == null)
            return all;
        if (!all.Contains(app))
            throw new YardException(
                $"Unknown {(backendsOnly ? "backend" : "application")}: {app}",
                2
            );
        return [app];
    }

    public string[] DevelopmentApps(string? app) {
        if (app == null)
            return Definition.Backends.Concat(Definition.Frontends).ToArray();
        _ = Select(app);
        var result = new HashSet<string>();
        void Add(string a) {
            if (!result.Add(a))
                return;
            foreach (var d in Definition.Dependencies.GetValueOrDefault(a, []))
                Add(d);
            if (Definition.Frontends.Contains(a))
                Add("api");
        }
        Add(app);
        return result.Order().ToArray();
    }

    public void Save() {
        File.WriteAllText(
            Path.Combine(Root, "yard.json"),
            JsonSerializer.Serialize(Definition, Json) + "\n"
        );
    }

    public IReadOnlyList<string> StructureErrors() {
        var errors = new List<string>();
        foreach (var app in Definition.Backends) {
            if (!File.Exists(Project(app)))
                errors.Add("Missing standard backend: " + Path.GetRelativePath(Root, Project(app)));
            else if (
                Directory
                    .GetFiles(
                        Path.Combine(AppDirectory(app), "src"),
                        "*.csproj",
                        SearchOption.AllDirectories
                    )
                    .Length != 1
            )
                errors.Add($"Backend {app} must contain exactly one production project.");
        }
        foreach (var app in Definition.Frontends)
            if (!File.Exists(Path.Combine(AppDirectory(app), "package.json")))
                errors.Add($"Missing frontend apps/{app}/package.json");
        if (Directory.Exists(Path.Combine(Root, "apps")))
            foreach (var app in Directory.GetDirectories(Path.Combine(Root, "apps")))
                if (
                    !Definition
                        .Backends.Concat(Definition.Frontends)
                        .Contains(Path.GetFileName(app))
                )
                    errors.Add("Undeclared application: " + Path.GetFileName(app));
        if (
            File.Exists(Path.Combine(Root, "Taskfile.yml"))
            || File.Exists(Path.Combine(Root, "Taskfile.yaml"))
            || File.Exists(Path.Combine(Root, "Procfile"))
        )
            errors.Add("Taskfile and Procfile are not supported in a Yard application.");
        return errors;
    }
}
