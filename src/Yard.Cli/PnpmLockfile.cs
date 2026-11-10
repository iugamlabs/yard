using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using Yard;

namespace Yard.Cli;

internal static class PnpmLockfile {
    public static string AddFrontend(string current, string template, string name) {
        try {
            var stream = new YamlStream();
            stream.Load(new StringReader(current));
            var bundled = new YamlStream();
            bundled.Load(new StringReader(template));
            var root = WorkspaceDocument(stream);
            var source = WorkspaceDocument(bundled);
            if (
                root["lockfileVersion"].ToString() != "9.0"
                || source["lockfileVersion"].ToString() != "9.0"
            )
                throw new YardException("Unsupported pnpm lockfile version; expected 9.0.");
            var importers = (YamlMappingNode)root["importers"];
            var key = new YamlScalarNode("apps/" + name);
            if (importers.Children.ContainsKey(key))
                throw new YardException(
                    "The frontend already has an importer in pnpm-lock.yaml: " + name
                );
            importers.Add(key, ((YamlMappingNode)source["importers"])["apps/web"]);
            // Copy locked resolutions, including peer and optional dependency snapshots.
            // Existing project entries always win; no dependency versions are resolved here.
            foreach (var section in new[] { "packages", "snapshots" }) {
                var target = (YamlMappingNode)root[section];
                foreach (var entry in (YamlMappingNode)source[section])
                    if (!target.Children.ContainsKey(entry.Key))
                        target.Add(entry.Key, entry.Value);
            }
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            stream.Save(writer, assignAnchors: false);
            // pnpm recognizes its package-manager document only with an explicit first marker.
            return "---\n" + writer.ToString();
        }
        catch (Exception error)
            when (error
                    is YamlException
                        or InvalidCastException
                        or KeyNotFoundException
                        or ArgumentException
            ) {
            throw new YardException("Invalid pnpm-lock.yaml: " + error.Message);
        }
    }

    private static YamlMappingNode WorkspaceDocument(YamlStream stream) {
        var documents = stream
            .Documents.Select(d => d.RootNode)
            .OfType<YamlMappingNode>()
            .Where(root =>
                root.Children.TryGetValue(new YamlScalarNode("importers"), out var node)
                && node is YamlMappingNode importers
                && importers.Children.ContainsKey(new YamlScalarNode("apps/web"))
            )
            .ToArray();
        if (documents.Length != 1)
            throw new YardException(
                "Expected one workspace document with an apps/web importer in pnpm-lock.yaml."
            );
        return documents[0];
    }
}
