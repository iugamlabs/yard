#!/usr/bin/env python3
"""Accept an installed Yard distribution; all fixtures and records stay in .temp."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--tool", type=Path, required=True)
parser.add_argument("--database", choices=["sqlite", "postgresql"], default="sqlite")
parser.add_argument("--multi", action="store_true")
parser.add_argument("--backends", type=int, choices=[1, 2], default=1)
parser.add_argument("--frontends", type=int, choices=[1, 2], default=1)
parser.add_argument("--skip-browser", action="store_true")
args = parser.parse_args()
tool = args.tool.resolve()
case = ROOT / ".temp" / "acceptance" / (args.database + "-" + uuid.uuid4().hex[:8])
logs = case / "logs"
logs.mkdir(parents=True)
project = case / "project"
checks = []
completed = False

def run(label, command, *, cwd=project, input=None, expected=0):
    with (logs / (label + ".log")).open("w") as output:
        result = subprocess.run([str(x) for x in command], cwd=cwd, input=input, text=True,
                                stdout=output, stderr=subprocess.STDOUT, timeout=900)
    checks.append({"name": label, "exitCode": result.returncode, "passed": result.returncode == expected})
    print(label + ": " + ("PASS" if result.returncode == expected else "FAIL"), flush=True)
    if result.returncode != expected:
        raise RuntimeError(label + " failed; inspect " + str(logs / (label + ".log")))

def yard(label, *command, **kwargs):
    run(label, [tool, *command], **kwargs)

def source_hashes():
    return {str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in project.rglob("*") if p.is_file()
            and not {".yard", "node_modules", "bin", "obj", "dist"}.intersection(p.relative_to(project).parts)}

try:
    yard("new", "new", "MatrixProject", "--database", args.database, "--output", project, cwd=ROOT)
    assert not (project / ".yard").exists() and not (project / "node_modules").exists()
    yard("refuse-overwrite", "new", "MatrixProject", "--output", project, cwd=ROOT, expected=2)
    if args.multi or args.backends == 2:
        yard("add-backend", "add", "backend", "manager")
        manifest = project / "yard.json"
        topology = json.loads(manifest.read_text())
        topology["dependencies"] = {"api": ["manager"]}
        manifest.write_text(json.dumps(topology, indent=2) + "\n")
        yard("sync-topology", "sync")
    if args.multi or args.frontends == 2:
        yard("add-frontend", "add", "frontend", "admin")
    yard("init", "init")
    before = (project / ".yard" / "settings.json").read_bytes()
    yard("repeat-init", "init")
    assert (project / ".yard" / "settings.json").read_bytes() == before
    yard("bootstrap", "user", "bootstrap", "owner", "--password-stdin", input="Acceptance-password-123\n")
    yard("repeat-bootstrap", "user", "bootstrap", "owner", "--password-stdin", input="Different-password-123\n", expected=1)
    snapshot = source_hashes()
    yard("sync-check", "sync", "--check")
    yard("upgrade-plan", "upgrade", "--plan")
    yard("upgrade-noop", "upgrade")
    assert source_hashes() == snapshot
    config = project / ".prettierrc.json"
    original = config.read_text()
    config.write_text(original + " ")
    yard("drift-check", "sync", "--check", expected=1)
    yard("upgrade-conflict-plan", "upgrade", "--plan")
    yard("upgrade-conflict", "upgrade", expected=1)
    assert config.read_text() == original + " "
    config.write_text(original)
    metadata = project / "yard.json"
    valid = metadata.read_text()
    invalid = json.loads(valid)
    invalid["commands"] = {}
    metadata.write_text(json.dumps(invalid))
    yard("unknown-config", "doctor", expected=2)
    metadata.write_text(valid)
    yard("doctor", "doctor")
    yard("check", "check")
    schema = json.loads((project / "packages/api-client/openapi.json").read_text())
    assert len(schema["paths"]) >= 40
    if not args.skip_browser:
        yard("verify", "verify")
    # Add a real business-model change and verify both provider migration chains.
    context = project / "apps/api/src/MatrixProject.Api/Infrastructure/AppDbContext.cs"
    text = context.read_text().replace(": YardDbContext(options, scope);", ": YardDbContext(options, scope) { public DbSet<MigrationProbe> MigrationProbes => Set<MigrationProbe>(); }\npublic sealed class MigrationProbe { public int Id {get;set;} public string Value {get;set;}=\"\"; }")
    context.write_text(text)
    yard("incremental-generate", "db", "generate", "AcceptanceIncrement")
    yard("incremental-migrate", "db", "migrate")
    yard("repeat-migrate", "db", "migrate")
    yard("migration-model", "db", "check", "--all")
    yard("target-status", "db", "status", "--app", "api")
    if args.multi or args.backends == 2:
        yard("internal-status", "db", "status", "--app", "manager")
    yard("database-tests", "test", "--app", "api")
    completed = True
finally:
    if (project / ".yard/settings.json").exists():
        for command in [[str(tool), "dev", "stop"], [str(tool), "db", "down"]]:
            try:
                subprocess.run(command, cwd=project, capture_output=True, timeout=45)
            except subprocess.TimeoutExpired:
                checks.append({"name": "cleanup", "passed": False})
    (case / "results.json").write_text(json.dumps({"database": args.database,
        "backends": 2 if args.multi else args.backends,
        "frontends": 2 if args.multi else args.frontends,
        "checks": checks, "passed": completed and bool(checks) and all(x["passed"] for x in checks)}, indent=2) + "\n")
    print("Acceptance record: " + str(case / "results.json"), flush=True)
