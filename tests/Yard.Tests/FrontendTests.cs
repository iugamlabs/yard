using System.Text.Json.Nodes;
using Xunit;
using YamlDotNet.RepresentationModel;
using Yard;
using Yard.Cli;

namespace Yard.Tests;

public sealed class FrontendTests {
    private static async Task<Workspace> Create() {
        var root = Path.Combine(
            WorkspaceTests.FindRoot(),
            ".temp",
            "frontend-tests",
            Guid.NewGuid().ToString("N")
        );
        return await Templates.Create("FrontendTest", root, "sqlite", CancellationToken.None);
    }

    private static YamlMappingNode MainDocument(YamlStream stream) =>
        (YamlMappingNode)stream.Documents.Last().RootNode;

    private static YamlStream ReadLock(Workspace workspace) {
        var stream = new YamlStream();
        stream.Load(
            new StringReader(File.ReadAllText(Path.Combine(workspace.Root, "pnpm-lock.yaml")))
        );
        return stream;
    }

    private static void WriteLock(Workspace workspace, YamlStream stream) {
        using var writer = new StringWriter();
        stream.Save(writer, false);
        File.WriteAllText(Path.Combine(workspace.Root, "pnpm-lock.yaml"), writer.ToString());
    }

    private static Dictionary<string, string> Contents(Workspace workspace) =>
        Directory
            .GetFiles(workspace.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                file => Path.GetRelativePath(workspace.Root, file),
                file => Convert.ToBase64String(File.ReadAllBytes(file))
            );

    private static void Unchanged(Workspace workspace, Dictionary<string, string> before) {
        var after = Contents(workspace);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, content) in before)
            Assert.Equal(content, after[path]);
        Assert.False(Directory.Exists(workspace.AppDirectory("admin")));
    }

    [Fact]
    public async Task AddedFrontendUsesTemplateDependenciesAndPreservesEvolvedProjectLock() {
        var workspace = await Create();
        var package = Path.Combine(workspace.AppDirectory("web"), "package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(package))!;
        manifest["dependencies"]!["clsx"] = "2.1.1";
        File.WriteAllText(package, manifest.ToJsonString());
        var stream = ReadLock(workspace);
        var original = MainDocument(stream);
        var web = (YamlMappingNode)((YamlMappingNode)original["importers"])["apps/web"];
        ((YamlMappingNode)web["dependencies"]).Add(
            "clsx",
            new YamlMappingNode { { "specifier", "2.1.1" }, { "version", "2.1.1" } }
        );
        WriteLock(workspace, stream);
        Templates.Add(workspace, "frontend", "admin");
        Assert.StartsWith(
            "---\n",
            File.ReadAllText(Path.Combine(workspace.Root, "pnpm-lock.yaml"))
        );
        var result = ReadLock(workspace);
        var updated = MainDocument(result);
        var admin = (YamlMappingNode)((YamlMappingNode)updated["importers"])["apps/admin"];
        Assert.False(
            ((YamlMappingNode)admin["dependencies"]).Children.ContainsKey(
                new YamlScalarNode("clsx")
            )
        );
        Assert.Null(
            JsonNode.Parse(
                File.ReadAllText(Path.Combine(workspace.AppDirectory("admin"), "package.json"))
            )!["dependencies"]!["clsx"]
        );
        Assert.Equal(web, ((YamlMappingNode)updated["importers"])["apps/web"]);
        Assert.Equal(stream.Documents.First().RootNode, result.Documents.First().RootNode);
        foreach (var section in new[] { "packages", "snapshots" })
            foreach (var (key, value) in (YamlMappingNode)original[section])
                Assert.Equal(value, ((YamlMappingNode)updated[section])[key]);
        Assert.Empty(Synchronization.Apply(Workspace.Load(workspace.Root), true));
    }

    [Fact]
    public async Task TemplateResolutionsAreRestoredWhenAbsentFromCurrentLock() {
        var workspace = await Create();
        var stream = ReadLock(workspace);
        var root = MainDocument(stream);
        ((YamlMappingNode)root["packages"]).Children.Remove(new YamlScalarNode("react@19.3.0"));
        ((YamlMappingNode)root["snapshots"]).Children.Remove(new YamlScalarNode("react@19.3.0"));
        WriteLock(workspace, stream);
        Templates.Add(workspace, "frontend", "admin");
        var result = MainDocument(ReadLock(workspace));
        Assert.True(
            ((YamlMappingNode)result["packages"]).Children.ContainsKey(
                new YamlScalarNode("react@19.3.0")
            )
        );
        Assert.True(
            ((YamlMappingNode)result["snapshots"]).Children.ContainsKey(
                new YamlScalarNode("react@19.3.0")
            )
        );
    }

    [Theory]
    [InlineData("missing-importer")]
    [InlineData("malformed")]
    [InlineData("duplicate-admin")]
    public async Task InvalidLockDoesNotLeaveAnApplication(string defect) {
        var workspace = await Create();
        var path = Path.Combine(workspace.Root, "pnpm-lock.yaml");
        var text = File.ReadAllText(path);
        text = defect switch {
            "missing-importer" => text.Replace("  apps/web:", "  apps/renamed:"),
            "malformed" => "importers: [",
            _ => text.Replace("  apps/web:", "  apps/admin: {}\n  apps/web:"),
        };
        File.WriteAllText(path, text);
        var before = Contents(workspace);
        Assert.Throws<YardException>(() => Templates.Add(workspace, "frontend", "admin"));
        Unchanged(workspace, before);
    }

    [Fact]
    public async Task CrLfLockfilesAndRepeatedAddsRemainValid() {
        var workspace = await Create();
        var path = Path.Combine(workspace.Root, "pnpm-lock.yaml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\n", "\r\n"));
        Templates.Add(workspace, "frontend", "admin");
        Templates.Add(Workspace.Load(workspace.Root), "frontend", "portal");
        var importers = (YamlMappingNode)MainDocument(ReadLock(workspace))["importers"];
        Assert.Equal(importers["apps/admin"], importers["apps/portal"]);
        Assert.Equal(
            new[] { "web", "admin", "portal" },
            Workspace.Load(workspace.Root).Definition.Frontends
        );
    }

    [Fact]
    public async Task InvalidPrivateSettingsDoNotPartiallyAddAnApplication() {
        var workspace = await Create();
        LocalSettings.Configure(workspace);
        var path = LocalSettings.PathFor(workspace);
        var settings = JsonNode.Parse(File.ReadAllText(path))!;
        settings["identity"] = "different-checkout";
        File.WriteAllText(path, settings.ToJsonString());
        var before = Contents(workspace);
        Assert.Throws<YardException>(() => Templates.Add(workspace, "frontend", "admin"));
        Unchanged(workspace, before);
    }

    [Theory]
    [InlineData("frontend")]
    [InlineData("backend")]
    public async Task WriteFailureRestoresTheManifestLockAndGeneratedFiles(string kind) {
        var workspace = await Create();
        LocalSettings.Configure(workspace);
        var toolManifest = Path.Combine(workspace.Root, ".config", "dotnet-tools.json");
        File.Delete(toolManifest);
        Directory.CreateDirectory(toolManifest);
        var before = Contents(workspace);
        var directories = Directory
            .GetDirectories(workspace.Root, "*", SearchOption.AllDirectories)
            .Order()
            .ToArray();
        Assert.NotNull(Record.Exception(() => Templates.Add(workspace, kind, "admin")));
        Unchanged(workspace, before);
        Assert.Equal(
            directories,
            Directory
                .GetDirectories(workspace.Root, "*", SearchOption.AllDirectories)
                .Order()
                .ToArray()
        );
    }

    [Fact]
    public async Task AddedBackendIncludesItsTestsAndCheckoutSettings() {
        var workspace = await Create();
        var original = LocalSettings.Configure(workspace);
        Templates.Add(workspace, "backend", "worker");
        var next = Workspace.Load(workspace.Root);
        var settings = LocalSettings.Load(next);
        Assert.Equal(original.ControlToken, settings.ControlToken);
        Assert.Equal(original.Ports["api"], settings.Ports["api"]);
        Assert.Contains("worker", settings.Ports.Keys);
        Assert.Contains("worker", settings.Connections.Keys);
        Assert.Contains(
            "FrontendTest.Worker.Api.Tests.csproj",
            File.ReadAllText(Path.Combine(workspace.Root, "FrontendTest.slnx"))
        );
        Assert.Empty(Synchronization.Apply(next, true));
    }

    [Fact]
    public async Task ToolchainAllowsBusinessDependenciesAndScriptsButRejectsToolVersionDrift() {
        var workspace = await Create();
        var path = Path.Combine(workspace.Root, "package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["devDependencies"]!["business-tool"] = "1.2.3";
        manifest["scripts"] = new JsonObject { ["build"] = "echo custom" };
        File.WriteAllText(path, manifest.ToJsonString());
        FrontendToolchain.Validate(workspace);
        manifest["devDependencies"]!["typescript"] = "0.0.1";
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Contains(
            "typescript",
            Assert.Throws<YardException>(() => FrontendToolchain.Validate(workspace)).Message
        );
        Synchronization.Apply(workspace, false);
        Assert.Equal(
            "1.2.3",
            JsonNode.Parse(File.ReadAllText(path))!["devDependencies"]![
                "business-tool"
            ]!.GetValue<string>()
        );
    }

    [Fact]
    public async Task ManagedTypeScriptConfigurationCannotSilentlyDisableChecks() {
        var workspace = await Create();
        var path = Path.Combine(workspace.AppDirectory("web"), "tsconfig.json");
        File.WriteAllText(path, "{\"files\":[]}");
        Assert.Contains(
            "tsconfig.json",
            Assert.Throws<YardException>(() => FrontendToolchain.Validate(workspace)).Message
        );
        Synchronization.Apply(workspace, false);
        FrontendToolchain.Validate(workspace);
    }

    [Theory]
    [InlineData("apps/web")]
    [InlineData("packages/ui")]
    public async Task WorkspaceDependenciesCannotShadowStandardTools(string relative) {
        var workspace = await Create();
        var path = Path.Combine(workspace.Root, relative, "package.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["devDependencies"] = new JsonObject { ["@vitejs/plugin-react"] = "0.0.1" };
        File.WriteAllText(path, manifest.ToJsonString());
        Assert.Contains(
            "cannot override",
            Assert.Throws<YardException>(() => FrontendToolchain.Validate(workspace)).Message
        );
    }

    [Fact]
    public async Task ExecutedToolMustHaveThePinnedInstalledVersion() {
        var workspace = await Create();
        var directory = Path.Combine(workspace.Root, "node_modules", "typescript");
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        File.WriteAllText(Path.Combine(directory, "package.json"), "{\"version\":\"0.0.1\"}");
        File.WriteAllText(Path.Combine(directory, "bin", "tsc"), "console.log('fake');");
        Assert.Contains(
            "Installed typescript",
            Assert
                .Throws<YardException>(() =>
                    FrontendToolchain.Entrypoint(workspace, "typescript", "bin/tsc")
                )
                .Message
        );
    }
}
