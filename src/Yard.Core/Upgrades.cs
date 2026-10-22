using System.Security.Cryptography;

namespace Yard;

public sealed record MigrationFile(string Path, string? ExpectedHash, string Content);

public interface IWorkspaceMigration {
    string From { get; }
    string To { get; }
    IReadOnlyList<MigrationFile> Files(Workspace workspace);
}

public sealed record UpgradeChange(
    string Path,
    string? BeforeHash,
    string AfterHash,
    string Content,
    bool Conflict
);

public sealed record UpgradePlan(
    string From,
    string To,
    bool Supported,
    IReadOnlyList<UpgradeChange> Changes
) {
    public bool HasConflicts => Changes.Any(x => x.Conflict);
}

public sealed class Upgrades(IEnumerable<IWorkspaceMigration>? registered = null) {
    private readonly IWorkspaceMigration[] migrations = registered?.ToArray() ?? [];

    public static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    public UpgradePlan Plan(Workspace workspace, string target) {
        var source = workspace.Definition.ToolVersion;
        if (source == target) {
            var drift = Synchronization
                .Expected(workspace)
                .Where(x =>
                    !File.Exists(Path.Combine(workspace.Root, x.Key))
                    || File.ReadAllText(Path.Combine(workspace.Root, x.Key)) != x.Value
                )
                .Select(x => new UpgradeChange(
                    x.Key,
                    ReadHash(workspace, x.Key),
                    Hash(x.Value),
                    x.Value,
                    true
                ))
                .ToArray();
            return new(source, target, true, drift);
        }
        var route = Find(source, target, new HashSet<string>());
        if (route == null)
            return new(source, target, false, []);
        var changes = new Dictionary<string, UpgradeChange>();
        foreach (var migration in route)
            foreach (var file in migration.Files(workspace)) {
                var full = Path.GetFullPath(Path.Combine(workspace.Root, file.Path));
                if (
                    !full.StartsWith(
                        workspace.Root + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal
                    )
                    || file.Path == "yard.json"
                )
                    throw new YardException("A migration attempted to modify an invalid path.");
                var before = changes.TryGetValue(file.Path, out var earlier)
                    ? earlier.AfterHash
                    : ReadHash(workspace, file.Path);
                changes[file.Path] = new(
                    file.Path,
                    earlier != null ? earlier.BeforeHash : before,
                    Hash(file.Content),
                    file.Content,
                    earlier?.Conflict == true || before != file.ExpectedHash
                );
            }
        return new(source, target, true, changes.Values.ToArray());
    }

    private IWorkspaceMigration[]? Find(string from, string to, HashSet<string> visited) {
        if (from == to)
            return [];
        if (!visited.Add(from))
            return null;
        foreach (var migration in migrations.Where(x => x.From == from)) {
            var tail = Find(migration.To, to, new HashSet<string>(visited));
            if (tail != null)
                return [migration, .. tail];
        }
        return null;
    }

    private static string? ReadHash(Workspace workspace, string path) =>
        File.Exists(Path.Combine(workspace.Root, path))
            ? Hash(File.ReadAllText(Path.Combine(workspace.Root, path)))
            : null;

    public void Apply(Workspace workspace, UpgradePlan plan) {
        if (workspace.Definition.ToolVersion != plan.From || !plan.Supported)
            throw new YardException(
                $"No migration from {plan.From} to {plan.To} is registered; no files were changed."
            );
        if (plan.HasConflicts)
            throw new YardException(
                "Upgrade conflicts with modified managed files. Review the plan and use yard sync for intentional standardization."
            );
        if (plan.Changes.Any(x => ReadHash(workspace, x.Path) != x.BeforeHash))
            throw new YardException("Files changed after the upgrade plan; no files were changed.");
        var backups = plan.Changes.ToDictionary(
            x => x.Path,
            x =>
                File.Exists(Path.Combine(workspace.Root, x.Path))
                    ? File.ReadAllBytes(Path.Combine(workspace.Root, x.Path))
                    : null
        );
        var manifest = File.ReadAllBytes(Path.Combine(workspace.Root, "yard.json"));
        try {
            foreach (var file in plan.Changes) {
                var path = Path.Combine(workspace.Root, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }
            if (plan.From != plan.To)
                new Workspace(
                    workspace.Root,
                    workspace.Definition with {
                        ToolVersion = plan.To,
                    }
                ).Save();
        }
        catch {
            foreach (var file in backups) {
                var path = Path.Combine(workspace.Root, file.Key);
                if (file.Value == null)
                    File.Delete(path);
                else
                    File.WriteAllBytes(path, file.Value);
            }
            File.WriteAllBytes(Path.Combine(workspace.Root, "yard.json"), manifest);
            throw;
        }
    }
}
