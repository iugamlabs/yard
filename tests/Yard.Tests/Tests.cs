using Xunit;
using Yard;

namespace Yard.Tests;

public sealed class WorkspaceTests {
    [Fact]
    public void RejectsUnknownProperties() {
        var root = Temp();
        File.WriteAllText(Path.Combine(root, "yard.json"), "{\"name\":\"Test\",\"commands\":{}}");
        Assert.Throws<YardException>(() => Workspace.Load(root));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Api")]
    [InlineData("bad_name")]
    [InlineData("web")]
    public void RejectsInvalidOrDuplicateApplication(string name) {
        var w = new Workspace(Temp(), new() { Name = "Test", Backends = ["api", name] });
        Assert.Throws<YardException>(w.Validate);
    }

    [Fact]
    public void DetectsCycles() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Dependencies = new() { ["api"] = ["manager"], ["manager"] = ["api"] },
            }
        );
        Assert.Throws<YardException>(w.Validate);
    }

    [Fact]
    public void BackendOrderAndAppSelectionAreStable() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Frontends = ["web", "admin"],
                Dependencies = new() { ["api"] = ["manager"] },
            }
        );
        w.Validate();
        Assert.Equal(["manager", "api"], w.OrderedBackends());
        Assert.Equal(["admin", "api", "manager"], w.DevelopmentApps("admin"));
        Assert.Equal("Test.Manager.Api", w.ProjectName("manager"));
    }

    [Fact]
    public void SettingsAreIdempotentAndCheckoutSpecific() {
        var root = Temp();
        var w = new Workspace(root, new() { Name = "Test", Database = "sqlite" });
        var a = LocalSettings.Configure(w);
        var b = LocalSettings.Configure(w);
        Assert.Equal(a.ControlToken, b.ControlToken);
        Assert.Equal(a.Ports["api"], b.Ports["api"]);
        Assert.NotEqual(a.Identity, new Workspace(Temp(), w.Definition).Identity);
        Assert.Contains(root, a.Connections["api"]);
    }

    [Fact]
    public void SyncCheckDoesNotWrite() {
        var w = new Workspace(Temp(), new() { Name = "Test" });
        Assert.NotEmpty(Synchronization.Apply(w, true));
        Assert.False(File.Exists(Path.Combine(w.Root, "Test.slnx")));
        Synchronization.Apply(w, false);
        Assert.Empty(Synchronization.Apply(w, true));
    }

    [Fact]
    public void TailwindSourcesOnlyFollowDeclaredFrontends() {
        var w = new Workspace(
            Temp(),
            new() {
                Name = "Test",
                Backends = ["api", "manager"],
                Frontends = ["web", "admin"],
            }
        );
        var sources = Synchronization.Expected(w)["packages/ui/src/sources.css"];
        Assert.Contains("apps/web/src/", sources);
        Assert.Contains("apps/admin/src/", sources);
        Assert.DoesNotContain("apps/api", sources);
        Assert.DoesNotContain("apps/manager", sources);
        Assert.DoesNotContain("apps/*", sources);
    }

    [Fact]
    public void CopiedControlSettingsCannotStopAnotherCheckout() {
        var source = new Workspace(Temp(), new() { Name = "Test", Database = "sqlite" });
        LocalSettings.Configure(source);
        var other = new Workspace(Temp(), source.Definition);
        Directory.CreateDirectory(other.State);
        File.Copy(LocalSettings.PathFor(source), LocalSettings.PathFor(other));
        Assert.Throws<YardException>(() => LocalSettings.Load(other));
    }

    [Fact]
    public void OldSchemaIsRejectedWithoutWriting() {
        var root = Temp();
        var file = Path.Combine(root, "yard.json");
        const string original =
            "{\"schemaVersion\":1,\"name\":\"OldProject\",\"toolVersion\":\"0.1.0\"}";
        File.WriteAllText(file, original);
        var error = Assert.Throws<YardException>(() => Workspace.TryFind(root));
        Assert.Contains("schema 2", error.Message);
        Assert.Equal(original, File.ReadAllText(file));
        Assert.False(Directory.Exists(Path.Combine(root, ".yard")));
    }

    [Fact]
    public void SyncPreservesBusinessFiles() {
        var w = new Workspace(Temp(), new() { Name = "Test" });
        var file = Path.Combine(w.Root, "apps", "api", "Business.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "business source");
        Synchronization.Apply(w, false);
        Assert.Equal("business source", File.ReadAllText(file));
        Assert.DoesNotContain(Synchronization.Expected(w).Keys, x => x.Contains("platform"));
    }

    private static string Temp() {
        var p = Path.Combine(FindRoot(), ".temp", "unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    public static string FindRoot() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Yard.slnx")))
            d = d.Parent;
        return d!.FullName;
    }
}
