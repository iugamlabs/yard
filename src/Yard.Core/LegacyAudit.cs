namespace Yard;

public static class LegacyAudit {
    public static string[] Inspect(string root) {
        var items = new List<string>
        {
            "Missing yard.json schema 1: adopt the fixed name/version/database/application topology manifest.",
        };
        if (!Directory.Exists(Path.Combine(root, "apps", "api")))
            items.Add(
                Directory.Exists(Path.Combine(root, "apps", "server"))
                    ? "apps/server exists: review moving the main backend to apps/api and the single production-project layout."
                    : "Missing standard apps/api backend."
            );
        if (!Directory.Exists(Path.Combine(root, "apps", "web")))
            items.Add("Missing standard apps/web frontend.");
        foreach (var file in new[] { "Taskfile.yml", "Taskfile.yaml", "Procfile" })
            if (File.Exists(Path.Combine(root, file)))
                items.Add(
                    file
                        + " exists: move development commands to Yard after reviewing equivalent behavior."
                );
        foreach (var package in new[] { "ui", "api-client", "platform" })
            if (!Directory.Exists(Path.Combine(root, "packages", package)))
                items.Add("Missing shared packages/" + package + ".");
        items.Add(
            "Review provider-specific EF migrations, fixed SDK/pnpm tools, organization isolation and standard account/mail configuration against a generated reference. No business code, configuration or database was changed."
        );
        return items.ToArray();
    }
}
