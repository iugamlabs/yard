using System.Text.Json;

namespace Yard;

public static class FrontendToolchain {
    private static string Resource(string name) {
        using var reader = new StreamReader(
            typeof(FrontendToolchain).Assembly.GetManifestResourceStream("Yard.Frontend/" + name)!
        );
        return reader.ReadToEnd();
    }

    public static string TypeScriptConfig { get; } = Resource("tsconfig.json");
    public static IReadOnlyDictionary<string, string> Dependencies { get; } = ReadDependencies();

    private static Dictionary<string, string> ReadDependencies() {
        using var document = JsonDocument.Parse(Resource("package.json"));
        return document
            .RootElement.GetProperty("devDependencies")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    public static void Validate(Workspace workspace) {
        var path = Path.Combine(workspace.Root, "package.json");
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (
                !root.TryGetProperty("packageManager", out var manager)
                || manager.GetString() != "pnpm@" + Versions.Pnpm
            )
                throw new YardException(
                    "package.json must pin packageManager to pnpm@" + Versions.Pnpm + "."
                );
            if (
                !root.TryGetProperty("engines", out var engines)
                || !engines.TryGetProperty("node", out var node)
                || node.GetString() != Versions.Node
            )
                throw new YardException(
                    "package.json must pin engines.node to " + Versions.Node + "."
                );
            foreach (var (name, version) in Dependencies)
                if (
                    !root.TryGetProperty("devDependencies", out var dependencies)
                    || !dependencies.TryGetProperty(name, out var actual)
                    || actual.GetString() != version
                )
                    throw new YardException(
                        $"package.json must pin the Yard toolchain dependency {name} to {version}; business dependencies may be added separately."
                    );
        }
        catch (Exception error)
            when (error is JsonException or InvalidOperationException or IOException) {
            throw new YardException("Invalid frontend package.json: " + error.Message);
        }
        var manifests = workspace.Definition.Frontends.Select(app =>
            Path.Combine(workspace.AppDirectory(app), "package.json")
        );
        var packages = Path.Combine(workspace.Root, "packages");
        if (Directory.Exists(packages))
            manifests = manifests.Concat(
                Directory
                    .GetDirectories(packages)
                    .Select(directory => Path.Combine(directory, "package.json"))
            );
        foreach (var manifest in manifests.Where(File.Exists)) {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            foreach (
                var section in new[] { "dependencies", "devDependencies", "optionalDependencies" }
            )
                if (document.RootElement.TryGetProperty(section, out var dependencies))
                    foreach (var (name, version) in Dependencies)
                        if (
                            dependencies.TryGetProperty(name, out var actual)
                            && actual.GetString() != version
                        )
                            throw new YardException(
                                $"{Path.GetRelativePath(workspace.Root, manifest)} cannot override the Yard toolchain dependency {name}; expected {version}."
                            );
        }
        var expected = Synchronization.Expected(workspace);
        foreach (var app in workspace.Definition.Frontends)
            foreach (var config in new[] { "tsconfig.json", "vite.config.ts" }) {
                var relative = $"apps/{app}/{config}";
                if (
                    !File.Exists(Path.Combine(workspace.Root, relative))
                    || File.ReadAllText(Path.Combine(workspace.Root, relative)) != expected[relative]
                )
                    throw new YardException(
                        "Managed frontend configuration differs: " + relative + ". Run yard sync."
                    );
            }
    }

    public static void ValidateInstalled(Workspace workspace) {
        foreach (var package in Dependencies.Keys)
            _ = InstalledDirectory(workspace, package);
    }

    private static string InstalledDirectory(Workspace workspace, string package) {
        var directory = Path.Combine(workspace.Root, "node_modules", package);
        var metadata = Path.Combine(directory, "package.json");
        if (!Dependencies.TryGetValue(package, out var version))
            throw new YardException("Unknown Yard frontend tool: " + package);
        if (!File.Exists(metadata))
            throw new YardException("Frontend tools are missing. Run yard install.");
        using var document = JsonDocument.Parse(File.ReadAllText(metadata));
        if (document.RootElement.GetProperty("version").GetString() != version)
            throw new YardException(
                $"Installed {package} must be {version}. Restore the locked dependencies with yard install."
            );
        return directory;
    }

    public static string Entrypoint(Workspace workspace, string package, string executable) {
        var directory = InstalledDirectory(workspace, package);
        var path = Path.Combine(directory, executable);
        if (!File.Exists(path))
            throw new YardException(
                "Frontend tool entrypoint is missing: " + package + ". Run yard install."
            );
        return path;
    }
}
