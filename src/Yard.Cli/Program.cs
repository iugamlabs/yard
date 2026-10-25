using System.CommandLine;
using System.Net;
using System.Text.Json;
using Spectre.Console;
using Yard;
using Yard.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => {
    e.Cancel = true;
    if (
        args.Where(x => x != "--json").Take(2).SequenceEqual(new[] { "db", "shell" })
        && !Console.IsInputRedirected
    )
        return;
    cancel.Cancel();
};
var root = new RootCommand("Yard — one convention for your C# + React monorepos");
var appOption = new Option<string?>("--app") { Description = "Select a declared application" };
var checkOption = new Option<bool>("--check") {
    Description = "Check without changing tracked files",
};
var version = new Command("version", "Show Yard version");
version.SetAction(_ => Console.WriteLine(Versions.Yard));
root.Add(version);
var jsonDiagnostics = new Option<bool>("--json") {
    Description = "Write structured error diagnostics to stderr",
    Recursive = true,
};
root.Add(jsonDiagnostics);
var create = new Command("new", "Generate a standard monorepo (files only)");
var name = new Argument<string>("name");
var output = new Option<string?>("--output", "-o");
var database = new Option<string>("--database") { DefaultValueFactory = _ => "postgresql" };
create.Add(name);
create.Add(output);
create.Add(database);
create.SetAction(async p => {
    var w = await Templates.Create(
        p.GetValue(name)!,
        p.GetValue(output),
        p.GetValue(database)!,
        cancel.Token
    );
    AnsiConsole.MarkupLine(
        "Created [green]" + Markup.Escape(w.Root) + "[/]. Run yard init to prepare development."
    );
});
root.Add(create);
var add = new Command("add", "Add a standard application");
var kind = new Argument<string>("kind");
var application = new Argument<string>("name");
add.Add(kind);
add.Add(application);
add.SetAction(p => {
    Templates.Add(Workspace.Find(), p.GetValue(kind)!, p.GetValue(application)!);
    Console.WriteLine("Application created. Run yard init to install and migrate.");
});
root.Add(add);
foreach (
    var action in new[]
    {
        "install",
        "init",
        "doctor",
        "build",
        "lint",
        "typecheck",
        "test",
        "check",
        "verify",
        "contracts",
    }
) {
    var command = new Command(action);
    if (action is "build" or "lint" or "typecheck" or "test" or "check")
        command.Add(appOption);
    if (action == "contracts")
        command.Add(checkOption);
    command.SetAction(async p => {
        var w = Workspace.Find();
        var c = new Commands(w, cancel.Token);
        switch (action) {
            case "install":
                await c.Install();
                break;
            case "init":
                await c.Initialize();
                break;
            case "doctor":
                await c.Doctor();
                break;
            case "build":
                await c.Build(p.GetValue(appOption));
                break;
            case "contracts":
                await c.Contracts(p.GetValue(checkOption));
                break;
            case "verify":
                await c.Verify();
                break;
            default:
                await c.Engineering(action, p.GetValue(appOption));
                break;
        }
    });
    root.Add(command);
}
var fmt = new Command("fmt", "Run the canonical formatting pipelines");
var file = new Option<string?>("--file");
fmt.Add(checkOption);
fmt.Add(file);
fmt.SetAction(async p =>
    await new Commands(Workspace.Find(), cancel.Token).Format(
        p.GetValue(checkOption),
        p.GetValue(file)
    )
);
root.Add(fmt);
var dev = new Command("dev", "Run the owned development environment");
dev.Add(appOption);
dev.SetAction(async p =>
    await new Commands(Workspace.Find(), cancel.Token).Dev(null, p.GetValue(appOption))
);
foreach (var action in new[] { "status", "stop", "restart" }) {
    var sub = new Command(action);
    sub.Add(appOption);
    sub.SetAction(async p =>
        await new Commands(Workspace.Find(), cancel.Token).Dev(action, p.GetValue(appOption))
    );
    dev.Add(sub);
}
root.Add(dev);
var db = new Command("db", "Manage the primary or explicitly selected backend database");
foreach (
    var action in new[]
    {
        "up",
        "down",
        "status",
        "generate",
        "migrate",
        "check",
        "flush",
        "shell",
        "sql",
    }
) {
    var sub = new Command(action);
    sub.Add(appOption);
    var all = new Option<bool>("--all");
    if (action is not ("shell" or "sql"))
        sub.Add(all);
    var yes = new Option<bool>("--yes") {
        Description = "Confirm deletion of all target database data",
    };
    var migrationPlan = new Option<bool>("--plan") {
        Description = "List pending migrations without applying them",
    };
    var sqlOutput = new Option<string?>("--output", "-o") {
        Description = "Write pending migration SQL to a file",
    };
    if (action == "flush")
        sub.Add(yes);
    if (action == "migrate")
        sub.Add(migrationPlan);
    if (action == "sql")
        sub.Add(sqlOutput);
    var migration = new Argument<string?>("name") { Arity = ArgumentArity.ZeroOrOne };
    if (action == "generate")
        sub.Add(migration);
    sub.SetAction(async p =>
        await new Commands(Workspace.Find(), cancel.Token).Database(
            action == "migrate" && p.GetValue(migrationPlan) ? "plan" : action,
            p.GetValue(appOption),
            p.GetValue(all),
            action == "generate" ? p.GetValue(migration) : null,
            action == "flush" && p.GetValue(yes),
            action == "sql" ? p.GetValue(sqlOutput) : null
        )
    );
    db.Add(sub);
}
root.Add(db);
var sync = new Command("sync", "Synchronize tool-owned files");
sync.Add(checkOption);
sync.SetAction(p => {
    var w = Workspace.Find();
    var changes = Synchronization.Apply(w, p.GetValue(checkOption));
    foreach (var path in changes)
        Console.WriteLine(path);
    if (p.GetValue(checkOption) && changes.Length > 0)
        throw new YardException("Managed files are out of date.");
});
root.Add(sync);
var upgrade = new Command("upgrade", "Plan or apply a standard version upgrade");
var plan = new Option<bool>("--plan");
var targetVersion = new Option<string>("--version") { DefaultValueFactory = _ => Versions.Yard };
upgrade.Add(plan);
upgrade.Add(targetVersion);
upgrade.SetAction(p => {
    var w = Workspace.Find();
    var v = p.GetValue(targetVersion);
    if (v != Versions.Yard)
        throw new YardException("Install Yard " + v + " to perform that version's migrations.");
    var service = new Upgrades();
    var migrationPlan = service.Plan(w, v!);
    Console.WriteLine(
        JsonSerializer.Serialize(
            new {
                from = migrationPlan.From,
                to = migrationPlan.To,
                supported = migrationPlan.Supported,
                files = migrationPlan.Changes.Select(x => new {
                    x.Path,
                    x.BeforeHash,
                    x.AfterHash,
                    x.Conflict,
                }),
            },
            Workspace.Json
        )
    );
    if (!p.GetValue(plan))
        service.Apply(w, migrationPlan);
});
root.Add(upgrade);
try {
    var config = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
    var parsed = root.Parse(args);
    if (parsed.Errors.Count > 0) {
        if (args.Contains("--json"))
            Console.Error.WriteLine(
                JsonSerializer.Serialize(
                    new {
                        code = "invalid_arguments",
                        message = string.Join("; ", parsed.Errors.Select(x => x.Message)),
                        exitCode = 2,
                    }
                )
            );
        else
            await parsed.InvokeAsync(config, cancel.Token);
        return 2;
    }
    // The global command is only a launcher inside a repository; a guard prevents delegation loops.
    if (
        Environment.GetEnvironmentVariable("YARD_LOCAL_EXECUTION") != "1"
        && args.FirstOrDefault() is not ("new" or "version" or "--help" or "-h")
    ) {
        var w = Workspace.TryFind();
        if (w != null) {
            if (args.FirstOrDefault() is "install" or "init") {
                Templates.Feed(w);
                await Processes.Run(
                    "dotnet",
                    ["tool", "restore", "--add-source", Path.Combine(w.State, "feed")],
                    w.Root,
                    cancel.Token
                );
            }
            var manifest = Path.Combine(w.Root, ".config", "dotnet-tools.json");
            using var pinned = JsonDocument.Parse(File.ReadAllText(manifest));
            if (
                pinned
                    .RootElement.GetProperty("tools")
                    .GetProperty("iugam.yard.cli")
                    .GetProperty("version")
                    .GetString() != w.Definition.ToolVersion
            )
                throw new YardException(
                    "Local tool version differs from yard.json. Review yard upgrade or restore the matching manifest.",
                    2
                );
            return await Processes.Inherit(
                "dotnet",
                ["tool", "run", "yard", .. args],
                w.Root,
                cancel.Token,
                new Dictionary<string, string?> { ["YARD_LOCAL_EXECUTION"] = "1" },
                args.FirstOrDefault() == "dev"
                && !args.Any(x => x is "status" or "stop" or "restart")
                    ? new Commands(w, CancellationToken.None).AwaitDevelopmentStop
                    : null
            );
        }
    }
    if (
        args.FirstOrDefault() == "init"
        && !File.Exists(Path.Combine(Environment.CurrentDirectory, "yard.json"))
    ) {
        if (Workspace.TryFind() == null) {
            Console.Error.WriteLine("Existing repository audit (no changes):");
            foreach (var issue in LegacyAudit.Inspect(Environment.CurrentDirectory))
                Console.Error.WriteLine("- " + issue);
            return 2;
        }
    }
    return await parsed.InvokeAsync(config, cancel.Token);
}
catch (OperationCanceledException) {
    return 130;
}
catch (YardException e) {
    Console.Error.WriteLine(
        args.Contains("--json")
            ? JsonSerializer.Serialize(
                new {
                    code = "yard_error",
                    message = e.Message,
                    exitCode = e.ExitCode,
                }
            )
            : e.Message
    );
    return e.ExitCode;
}
catch (Exception e) {
    Console.Error.WriteLine(
        args.Contains("--json")
            ? JsonSerializer.Serialize(
                new {
                    code = "operation_failed",
                    message = e.Message,
                    exitCode = 1,
                }
            )
            : e.Message
    );
    if (Environment.GetEnvironmentVariable("YARD_DEBUG") == "1")
        Console.Error.WriteLine(e);
    return 1;
}
