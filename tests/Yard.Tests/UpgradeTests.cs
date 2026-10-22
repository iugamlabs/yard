using Xunit;
using Yard;

namespace Yard.Tests;

public sealed record TestMigration(
    string From,
    string To,
    string Path,
    string? ExpectedHash,
    string Content
) : IWorkspaceMigration {
    public IReadOnlyList<MigrationFile> Files(Workspace workspace) =>
        [new(Path, ExpectedHash, Content)];
}

public sealed class UpgradeTests {
    private static Workspace Create() {
        var root = Path.Combine(
            WorkspaceTests.FindRoot(),
            ".temp",
            "upgrade",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var w = new Workspace(root, new() { Name = "UpgradeApp" });
        w.Save();
        Synchronization.Apply(w, false);
        return w;
    }

    [Fact]
    public void CurrentVersionUpgradeIsReadOnlyNoOp() {
        var w = Create();
        var manifest = File.ReadAllText(Path.Combine(w.Root, "yard.json"));
        var service = new Upgrades();
        var plan = service.Plan(w, Versions.Yard);
        Assert.True(plan.Supported);
        Assert.Empty(plan.Changes);
        service.Apply(w, plan);
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(w.Root, "yard.json")));
    }

    [Fact]
    public void CurrentVersionDriftIsAConflict() {
        var w = Create();
        var path = Path.Combine(w.Root, "global.json");
        File.WriteAllText(path, "manual change");
        var service = new Upgrades();
        var plan = service.Plan(w, Versions.Yard);
        Assert.True(plan.HasConflicts);
        Assert.Throws<YardException>(() => service.Apply(w, plan));
        Assert.Equal("manual change", File.ReadAllText(path));
    }

    [Fact]
    public void UnknownMigrationIsRejectedWithoutWrites() {
        var w = Create();
        var service = new Upgrades();
        var plan = service.Plan(w, "99.0.0");
        Assert.False(plan.Supported);
        Assert.Throws<YardException>(() => service.Apply(w, plan));
        Assert.Equal(Versions.Yard, Workspace.Load(w.Root).Definition.ToolVersion);
    }

    [Fact]
    public void RegisteredMigrationChecksExpectedHashAndAppliesVersion() {
        var w = Create();
        var path = Path.Combine(w.Root, ".prettierrc.json");
        var original = File.ReadAllText(path);
        var service = new Upgrades([
            new TestMigration(
                Versions.Yard,
                "99.0.0",
                ".prettierrc.json",
                Upgrades.Hash(original),
                "new managed content"
            ),
        ]);
        var plan = service.Plan(w, "99.0.0");
        Assert.False(plan.HasConflicts);
        service.Apply(w, plan);
        Assert.Equal("99.0.0", Workspace.Load(w.Root).Definition.ToolVersion);
        Assert.Equal("new managed content", File.ReadAllText(path));
    }

    [Fact]
    public void PostPlanEditsCannotBeOverwritten() {
        var w = Create();
        var path = Path.Combine(w.Root, ".prettierrc.json");
        var original = File.ReadAllText(path);
        var service = new Upgrades([
            new TestMigration(
                Versions.Yard,
                "99.0.0",
                ".prettierrc.json",
                Upgrades.Hash(original),
                "new managed content"
            ),
        ]);
        var plan = service.Plan(w, "99.0.0");
        File.WriteAllText(path, "concurrent edit");
        Assert.Throws<YardException>(() => service.Apply(w, plan));
        Assert.Equal("concurrent edit", File.ReadAllText(path));
        Assert.Equal(Versions.Yard, Workspace.Load(w.Root).Definition.ToolVersion);
    }
}
