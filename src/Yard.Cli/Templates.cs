using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.IDE;
using Yard;

namespace Yard.Cli;

public static class Templates {
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Templates");

    public static async Task<Workspace> Create(
        string name,
        string? output,
        string database,
        CancellationToken ct
    ) {
        var target = Path.GetFullPath(output ?? name);
        var w = new Workspace(target, new() { Name = name, Database = database });
        w.Validate();
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new YardException("Destination must be empty.", 2);
        using var host = new DefaultTemplateEngineHost("Iugam.Yard", Versions.Yard);
        using var engine = new Bootstrapper(
            host,
            loadDefaultComponents: true,
            virtualizeConfiguration: true
        );
        var installs = await engine.InstallTemplatePackagesAsync(
            [new InstallRequest(Root)],
            InstallationScope.Global,
            ct
        );
        if (installs.Any(x => !x.Success))
            throw new YardException("Failed to load the bundled Yard template.");
        var template = (await engine.GetTemplatesAsync(ct)).Single(x =>
            x.Identity == "Iugam.Yard.Monorepo"
        );
        var result = await engine.CreateAsync(
            template,
            name,
            target,
            new Dictionary<string, string?> { { "database", database } },
            cancellationToken: ct
        );
        if (result.Status.ToString() != "Success")
            throw new YardException("Template generation failed: " + result.ErrorMessage);
        w = Workspace.Load(target);
        Synchronization.Apply(w, false);
        return w;
    }

    public static void Feed(Workspace w) {
        var target = Path.Combine(w.State, "feed");
        Directory.CreateDirectory(target);
        var feed = Path.Combine(AppContext.BaseDirectory, "Feed");
        if (Directory.Exists(feed))
            foreach (var p in Directory.GetFiles(feed, "*.nupkg"))
                File.Copy(p, Path.Combine(target, Path.GetFileName(p)), true);
        // A tool installation retains the original nupkg beside its tools directory.
        var parent = new DirectoryInfo(AppContext.BaseDirectory);
        for (var n = 0; n < 6 && parent != null; n++, parent = parent.Parent)
            foreach (var file in parent.GetFiles("iugam.yard.cli.*.nupkg"))
                File.Copy(file.FullName, Path.Combine(target, file.Name), true);
    }

    public static void Add(Workspace w, string kind, string name) {
        if (kind is not ("backend" or "frontend"))
            throw new YardException("Use backend or frontend.", 2);
        var d =
            kind == "backend"
                ? w.Definition with {
                    Backends = [.. w.Definition.Backends, name],
                }
                : w.Definition with {
                    Frontends = [.. w.Definition.Frontends, name],
                };
        var next = new Workspace(w.Root, d);
        next.Validate();
        var target = next.AppDirectory(name);
        if (Directory.Exists(target))
            throw new YardException("Application directory already exists.", 2);
        var source = Path.Combine(Root, "apps", kind == "backend" ? "api" : "web");
        var sources =
            kind == "backend"
                ? new[] { source, Path.Combine(Root, "providers", w.Definition.Database) }
                : new[] { source };
        foreach (var tree in sources)
            foreach (var file in Directory.GetFiles(tree, "*", SearchOption.AllDirectories)) {
                var relative = Path.GetRelativePath(tree, file);
                if (
                    relative
                        .Split(Path.DirectorySeparatorChar)
                        .Any(x => x is "bin" or "obj" or "node_modules")
                )
                    continue;
                var replacement = kind == "backend" ? next.ProjectName(name) : w.Definition.Name;
                relative = relative.Replace("YardApp.Api", replacement);
                var text = File.ReadAllText(file)
                    .Replace(
                        "YardApp.Api",
                        kind == "backend" ? replacement : w.Definition.Name + ".Api"
                    )
                    .Replace(
                        "yardapp.api",
                        kind == "backend"
                            ? replacement.ToLowerInvariant()
                            : w.Definition.Name.ToLowerInvariant() + ".api"
                    )
                    .Replace("YardApp", w.Definition.Name)
                    .Replace("yardapp", w.Definition.Name.ToLowerInvariant())
                    .Replace("__DATABASE__", w.Definition.Database)
                    .Replace(
                        "__PROVIDER_PACKAGE__",
                        w.Definition.Database == "sqlite"
                            ? "Microsoft.EntityFrameworkCore.Sqlite"
                            : "Npgsql.EntityFrameworkCore.PostgreSQL"
                    );
                if (kind == "frontend" && Path.GetFileName(file) == "package.json")
                    text = text.Replace("/web\"", "/" + name + "\"");
                var p = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text);
            }
        if (kind == "frontend") {
            var lockfile = Path.Combine(w.Root, "pnpm-lock.yaml");
            var locked = File.ReadAllText(lockfile);
            var marker = "  apps/web:\n";
            var start = locked.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                throw new YardException("Missing web importer in pnpm-lock.yaml.");
            var end = locked.IndexOf("\n  ", start + marker.Length, StringComparison.Ordinal);
            while (end >= 0 && end + 3 < locked.Length && locked[end + 3] == ' ')
                end = locked.IndexOf("\n  ", end + 1, StringComparison.Ordinal);
            if (end < 0)
                throw new YardException("Invalid workspace lockfile.");
            var block = locked[start..end].Replace("  apps/web:", "  apps/" + name + ":");
            File.WriteAllText(lockfile, locked.Insert(end, "\n" + block));
        }
        next.Save();
        Synchronization.Apply(next, false);
        if (File.Exists(LocalSettings.PathFor(w)))
            LocalSettings.Configure(next);
    }
}
