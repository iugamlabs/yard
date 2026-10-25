#!/usr/bin/env python3
"""Exercise database commands from an installed Yard package in disposable .temp projects."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import pty
import re
import select
import signal
import sqlite3
import struct
import subprocess
import termios
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--tool', type=Path, required=True)
parser.add_argument('--database', choices=['sqlite', 'postgresql'], required=True)
args = parser.parse_args()
tool = args.tool.resolve()
case = ROOT / '.temp/database-acceptance' / (args.database + '-' + uuid.uuid4().hex[:8])
project = case / 'project'
other = case / 'other'
logs = case / 'logs'
logs.mkdir(parents=True)
checks = []
settings = None
completed = False


def run(label, *argv, expected=0, input=None, cwd=None, env=None):
    result = subprocess.run([str(x) for x in argv], cwd=cwd or project, input=input,
                            text=True, capture_output=True, timeout=900, env=env)
    (logs / (label + '.stdout')).write_text(result.stdout)
    (logs / (label + '.stderr')).write_text(result.stderr)
    checks.append({'name': label, 'exitCode': result.returncode, 'passed': result.returncode == expected})
    print(label + (': PASS' if result.returncode == expected else ': FAIL'), flush=True)
    assert result.returncode == expected, label + ': ' + result.stderr[-1500:]
    return result.stdout


def yard(label, *argv, **kwargs):
    return run(label, tool, *argv, **kwargs)


def sql(statement, app='api'):
    if args.database == 'sqlite':
        with sqlite3.connect(project / '.yard/data' / (app + '.db')) as connection:
            result = connection.execute(statement)
            return '\n'.join('|'.join(str(value) for value in row) for row in result.fetchall())
    result = subprocess.run(['docker', 'exec', 'yard-' + settings['identity'], 'psql', '-X', '-h',
                             '127.0.0.1', '-U', 'yard', '-d', 'yard_' + app, '-At',
                             '-v', 'ON_ERROR_STOP=1', '-c', statement], text=True,
                            capture_output=True, check=True, timeout=30)
    return result.stdout.strip()


def terminal(label, argv, text, expected=0, interrupt=False, env=None, wait_for=None, client_interrupt=None):
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 24, 100, 0, 0))
    def own_terminal():
        os.setsid()
        fcntl.ioctl(slave, termios.TIOCSCTTY, 0)
    process = subprocess.Popen([str(x) for x in argv], cwd=project, stdin=slave, stdout=slave,
                               stderr=slave, preexec_fn=own_terminal, env=env)
    os.close(slave)
    output = bytearray()
    try:
        deadline = time.monotonic() + 120
        sent = False
        while time.monotonic() < deadline:
            if not sent and (wait_for is None or wait_for in output):
                if text:
                    os.write(master, text.encode())
                if interrupt:
                    time.sleep(0.3)
                    os.killpg(process.pid, signal.SIGINT)
                if client_interrupt:
                    time.sleep(0.5)
                    os.write(master, b'\x03')
                    time.sleep(0.5)
                    os.write(master, client_interrupt.encode())
                sent = True
            if select.select([master], [], [], 0.1)[0]:
                try:
                    chunk = os.read(master, 65536)
                    if not chunk:
                        break
                    output.extend(chunk)
                except OSError:
                    break
            if process.poll() is not None:
                break
        process.wait(timeout=10)
        assert process.returncode == expected, label + ': ' + output.decode(errors='replace')[-1500:]
        checks.append({'name': label, 'passed': True, 'exitCode': process.returncode})
        print(label + ': PASS', flush=True)
        return output.decode(errors='replace')
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        os.close(master)
        (logs / (label + '.terminal')).write_bytes(output)


try:
    direct = dict(os.environ, YARD_LOCAL_EXECUTION='1')
    yard('new', 'new', 'DbCommands', '--database', args.database, '--output', project, cwd=ROOT)
    yard('add-backend', 'add', 'backend', 'manager', env=direct)
    manifest = project / 'yard.json'
    definition = json.loads(manifest.read_text())
    definition['dependencies'] = {'api': ['manager']}
    manifest.write_text(json.dumps(definition, indent=2) + '\n')
    yard('sync', 'sync', env=direct)
    yard('init', 'init')
    settings = json.loads((project / '.yard/settings.json').read_text())
    yard('other-checkout-new', 'new', 'DbCommands', '--database', args.database, '--output', other, cwd=ROOT)
    yard('other-checkout-init', 'init', cwd=other)
    yard('other-checkout-bootstrap', 'user', 'bootstrap', 'other', '--password-stdin', input='Acceptance-password-123\n', cwd=other)
    other_settings = json.loads((other / '.yard/settings.json').read_text())
    for verb in ['flush', 'migrate', 'status']:
        yard('mutually-exclusive-' + verb, 'db', verb, '--app', 'api', '--all', '--json', expected=2)
    for verb in ['flush', 'shell', 'sql']:
        extra = ['--yes'] if verb == 'flush' else []
        yard('reject-frontend-' + verb, 'db', verb, '--app', 'web', *extra, expected=2)
        yard('reject-unknown-' + verb, 'db', verb, '--app', 'unknown', *extra, expected=2)
    for verb in ['shell', 'sql']:
        yard('reject-all-' + verb, 'db', verb, '--all', expected=2)
    yard('bootstrap', 'user', 'bootstrap', 'owner', '--password-stdin', input='Acceptance-password-123\n')
    history = sql('SELECT * FROM "__EFMigrationsHistory" ORDER BY "MigrationId"')
    plan = yard('plan-all', 'db', 'migrate', '--plan', '--all')
    assert plan.index('Database plan: manager') < plan.index('Database plan: api')
    assert 'No pending migrations.' in plan
    assert yard('sql-noop', 'db', 'sql').startswith('-- No pending migrations.')
    yard('sql-output', 'db', 'sql', '-o', '.yard/pending.sql')
    assert (project / '.yard/pending.sql').read_text().startswith('-- No pending migrations.')
    yard('flush-needs-confirmation', 'db', 'flush', expected=2)
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'
    terminal('flush-cancel', [tool, 'db', 'flush'], '\n', expected=130, wait_for=b'Type yes')
    terminal('flush-confirmation-interrupt', [tool, 'db', 'flush'], '', expected=130, interrupt=True, wait_for=b'Type yes')
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'

    # Add raw SQL tables to both migration chains, including quoted names, cyclic FKs and an identity.
    yard('generate-probe', 'db', 'generate', 'DatabaseProbe')
    migration_files = list((project / 'apps/api').rglob('*_DatabaseProbe.cs'))
    assert len(migration_files) == 2
    for file in migration_files:
        postgres = 'Postgres' in file.parts
        identity = 'GENERATED BY DEFAULT AS IDENTITY' if postgres else 'PRIMARY KEY AUTOINCREMENT'
        statements = [
            f'CREATE TABLE "Raw ""parent" ("Id" INTEGER {identity}' + (', PRIMARY KEY ("Id")' if postgres else '') + ')',
            'CREATE TABLE "RawChild" ("Id" INTEGER PRIMARY KEY, "ParentId" INTEGER REFERENCES "Raw ""parent"("Id") ON DELETE RESTRICT)',
            'CREATE TABLE "CycleA" ("Id" INTEGER PRIMARY KEY, "OtherId" INTEGER' + ('' if postgres else ' REFERENCES "CycleB"("Id") ON DELETE RESTRICT') + ')',
            'CREATE TABLE "CycleB" ("Id" INTEGER PRIMARY KEY, "OtherId" INTEGER REFERENCES "CycleA"("Id") ON DELETE RESTRICT)',
        ]
        if postgres:
            statements.append('ALTER TABLE "CycleA" ADD FOREIGN KEY ("OtherId") REFERENCES "CycleB"("Id") ON DELETE RESTRICT')
        code = '\n'.join('            migrationBuilder.Sql(' + json.dumps(statement + ';') + ');' for statement in statements)
        source = file.read_text(encoding='utf-8-sig')
        source, count = re.subn(r'(protected override void Up\(MigrationBuilder migrationBuilder\)\s*\{)', lambda match: match[1] + '\n' + code, source)
        assert count == 1
        file.write_text(source)
    preview = yard('sql-pending', 'db', 'sql')
    assert preview.startswith(('BEGIN', 'START TRANSACTION', 'CREATE', 'PRAGMA'))
    assert 'RawChild' in preview and 'DatabaseProbe' in preview and 'Build succeeded' not in preview
    assert sql('SELECT * FROM "__EFMigrationsHistory" ORDER BY "MigrationId"') == history
    assert 'DatabaseProbe' in yard('plan-pending', 'db', 'migrate', '--plan')
    yard('flush-rejects-pending', 'db', 'flush', '--yes', expected=1)
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'
    yard('apply-probe', 'db', 'migrate')
    assert yard('sql-after-apply', 'db', 'sql').startswith('-- No pending migrations.')
    sql('INSERT INTO "Raw ""parent" DEFAULT VALUES')
    sql('INSERT INTO "RawChild" VALUES (1,1)')
    sql('INSERT INTO "CycleA" VALUES (1,NULL)')
    sql('INSERT INTO "CycleB" VALUES (1,1)')
    sql('UPDATE "CycleA" SET "OtherId"=1')
    boolean_false = 'false' if args.database == 'postgresql' else '0'
    sql('UPDATE "PlatformSettings" SET "RegistrationEnabled"=' + boolean_false)
    history = sql('SELECT * FROM "__EFMigrationsHistory" ORDER BY "MigrationId"')

    # A failure after deletion must restore every row and sequence.
    if args.database == 'sqlite':
        sql('CREATE TRIGGER FailSeed BEFORE INSERT ON PlatformSettings BEGIN SELECT RAISE(ABORT,\'controlled seed failure\'); END')
    else:
        sql('CREATE FUNCTION fail_seed() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION \'controlled seed failure\'; END $$')
        sql('CREATE TRIGGER fail_seed BEFORE INSERT ON "PlatformSettings" FOR EACH ROW EXECUTE FUNCTION fail_seed()')
    yard('flush-rolls-back', 'db', 'flush', '--yes', expected=1)
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'
    assert sql('SELECT COUNT(*) FROM "RawChild"') == '1'
    sql('DROP TRIGGER FailSeed' if args.database == 'sqlite' else 'DROP TRIGGER fail_seed ON "PlatformSettings"')
    if args.database == 'postgresql':
        sql('DROP FUNCTION fail_seed()')
    yard('flush', 'db', 'flush', '--yes')
    for table in ['AspNetUsers', 'RawChild', 'Raw "parent', 'CycleA', 'CycleB', 'IdentityAudits']:
        assert sql('SELECT COUNT(*) FROM "' + table.replace('"', '""') + '"') == '0', table
    assert sql('SELECT * FROM "__EFMigrationsHistory" ORDER BY "MigrationId"') == history
    assert sql('SELECT COUNT(*) FROM "IdentityLocks"') == '1'
    assert sql('SELECT "BootstrapCompleted" FROM "PlatformSettings"') in ['0', 'f']
    assert sql('SELECT "RegistrationEnabled" FROM "PlatformSettings"') in ['1', 't']
    sql('INSERT INTO "Raw ""parent" DEFAULT VALUES')
    assert sql('SELECT "Id" FROM "Raw ""parent"') == '1'
    yard('rebootstrap', 'user', 'bootstrap', 'owner', '--password-stdin', input='Acceptance-password-123\n')
    terminal('explicit-target-confirmed', [tool, 'db', 'flush', '--app', 'manager'], 'yes\n', wait_for=b'Type yes')
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'
    yard('flush-all', 'db', 'flush', '--all', '--yes')
    assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '0'
    assert 'other' in yard('other-checkout-preserved', 'user', 'list', cwd=other)

    # Both previews and real migration application must reject incompatible history/model state.
    sql('INSERT INTO "__EFMigrationsHistory" VALUES (\'20990101000000_Foreign\',\'10.0.13\')')
    for verb in ['migrate', 'sql', 'flush']:
        yard('foreign-history-' + verb, 'db', verb, *(['--yes'] if verb == 'flush' else []), expected=1)
    yard('foreign-history-plan', 'db', 'migrate', '--plan', expected=1)
    sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\'20990101000000_Foreign\'')
    first = history.splitlines()[0].split('|')[0]
    sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\'' + first + '\'')
    yard('non-contiguous-history', 'db', 'sql', expected=1)
    sql('INSERT INTO "__EFMigrationsHistory" VALUES (\'' + first + '\',\'10.0.13\')')
    context = project / 'apps/api/src/DbCommands.Api/Infrastructure/AppDbContext.cs'
    original = context.read_text()
    context.write_text(original.replace(': YardDbContext(options, scope);', ': YardDbContext(options, scope) { public DbSet<ModelDrift> Drift => Set<ModelDrift>(); }\npublic sealed class ModelDrift { public int Id { get; set; } }'))
    for verb in ['migrate', 'sql', 'flush']:
        yard('model-drift-' + verb, 'db', verb, *(['--yes'] if verb == 'flush' else []), expected=1)
    yard('model-drift-plan', 'db', 'migrate', '--plan', expected=1)
    context.write_text(original)

    # --all reports the committed first target when the second one fails.
    sql('INSERT INTO "__EFMigrationsHistory" VALUES (\'20990101000000_Foreign\',\'10.0.13\')')
    yard('partial-flush', 'db', 'flush', '--all', '--yes', expected=1)
    assert 'Completed database targets: manager' in (logs / 'partial-flush.stderr').read_text()
    sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\'20990101000000_Foreign\'')

    output = yard('shell-pipe', 'db', 'shell', input='SELECT 314159;\n')
    assert '314159' in output
    yard('shell-error', 'db', 'shell', input='INVALID SQL;\n', expected=3 if args.database == 'postgresql' else 1)
    quit_command = '\\q\n' if args.database == 'postgresql' else '.quit\n'
    output = terminal('shell-terminal', [tool, 'db', 'shell'], 'SELECT 271828;\n' + quit_command,
                      wait_for=b'yard_api=' if args.database == 'postgresql' else b'sqlite>')
    assert output.count('271828') >= 2
    output = yard('shell-selected-backend', 'db', 'shell', '--app', 'manager', input='SELECT COUNT(*) FROM "__EFMigrationsHistory";\n')
    assert '1' in output
    long_query = 'SELECT pg_sleep(30);\n' if args.database == 'postgresql' else 'WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT SUM(x) FROM n;\n'
    output = terminal('shell-query-interrupt', [tool, 'db', 'shell'], long_query,
                      expected=0 if args.database == 'postgresql' else 1,
                      wait_for=b'yard_api=' if args.database == 'postgresql' else b'sqlite>',
                      client_interrupt='SELECT 161803;\n' + quit_command)
    assert output.count('161803') >= 2
    if args.database == 'sqlite':
        # Restrict PATH to dotnet only to verify actionable missing-client diagnostics.
        missing = case / 'missing-client'
        missing.mkdir()
        (missing / 'dotnet').symlink_to(subprocess.check_output(['which', 'dotnet'], text=True).strip())
        yard('missing-client', 'db', 'shell', env=dict(os.environ, PATH=str(missing)), expected=1)
        assert 'Install sqlite3' in (logs / 'missing-client.stderr').read_text()
        database_file = project / '.yard/data/api.db'
        held = database_file.with_suffix('.held')
        database_file.rename(held)
        try:
            for verb in ['shell', 'sql', 'flush']:
                yard('missing-database-' + verb, 'db', verb, *(['--yes'] if verb == 'flush' else []), expected=1)
                assert not database_file.exists()
        finally:
            held.rename(database_file)
    else:
        # Hold an owned table lock so cancelling flush happens during its transaction.
        yard('before-cancel-bootstrap', 'user', 'bootstrap', 'cancel', '--password-stdin', input='Acceptance-password-123\n')
        locked = subprocess.Popen(['docker', 'exec', '-i', 'yard-' + settings['identity'], 'psql',
                                   '-X', '-h', '127.0.0.1', '-U', 'yard', '-d', 'yard_api', '-At'],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            locked.stdin.write('BEGIN; LOCK TABLE "AspNetUsers" IN ACCESS EXCLUSIVE MODE; SELECT \'LOCKED\';\n')
            locked.stdin.flush()
            while locked.stdout.readline().strip() != 'LOCKED':
                assert locked.poll() is None
            terminal('cancel-flush-transaction', [tool, 'db', 'flush', '--yes'], '', expected=130, interrupt=True, wait_for=b'Build succeeded')
        finally:
            locked.communicate('ROLLBACK;\n', timeout=15)
        yard('after-cancel', 'db', 'migrate', '--plan')
        assert sql('SELECT COUNT(*) FROM "AspNetUsers"') == '1'
        # Replace only this fixture's container with one carrying a deliberately wrong owner label.
        yard('stop-before-owner-test', 'db', 'down')
        run('remove-owned-container', 'docker', 'rm', 'yard-' + settings['identity'])
        run('create-wrong-owner', 'docker', 'create', '--name', 'yard-' + settings['identity'], '--label', 'iugam.yard.checkout=wrong', 'postgres:18.6')
        yard('reject-wrong-owner', 'db', 'shell', expected=1)
        assert 'ownership does not match' in (logs / 'reject-wrong-owner.stderr').read_text()
    completed = True
finally:
    for fixture in [project, other]:
        if (fixture / '.yard/settings.json').exists():
            subprocess.run([str(tool), 'db', 'down'], cwd=fixture, capture_output=True, timeout=60)
            if args.database == 'postgresql':
                owned = json.loads((fixture / '.yard/settings.json').read_text())['identity']
                subprocess.run(['docker', 'rm', '-f', 'yard-' + owned], capture_output=True, timeout=60)
                subprocess.run(['docker', 'volume', 'rm', 'yard-' + owned], capture_output=True, timeout=60)
    (case / 'results.json').write_text(json.dumps({'database': args.database, 'project': str(project),
        'checks': checks, 'passed': completed and all(x['passed'] for x in checks)}, indent=2) + '\n')
    print('Database acceptance record: ' + str(case / 'results.json'), flush=True)
