#!/usr/bin/env python3
"""Exercise migration conflict, ordered partial completion, rollback and recovery in a .temp fixture."""
import argparse
import json
from pathlib import Path
import re
import sqlite3
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--tool', type=Path, required=True)
p.add_argument('--project', type=Path, required=True)
args = p.parse_args()
tool, project = args.tool.resolve(), args.project.resolve()
assert project.is_relative_to(ROOT / '.temp'), 'Only Yard acceptance fixtures may be changed'
settings = json.loads((project / '.yard/settings.json').read_text())
manifest = json.loads((project / 'yard.json').read_text())
case = ROOT / '.temp/migration-acceptance' / uuid.uuid4().hex[:8]
case.mkdir(parents=True)
checks = []
completed = False

def run(label, *argv, expected=0):
    with (case / (label + '.log')).open('w') as output:
        r = subprocess.run([str(tool), *argv], cwd=project, stdout=output, stderr=subprocess.STDOUT, timeout=180)
    checks.append({'name': label, 'passed': r.returncode == expected, 'exitCode':r.returncode})
    print(label + (': PASS' if r.returncode == expected else ': FAIL'), flush=True)
    assert r.returncode == expected, label

def sql(statement):
    if manifest['database'] == 'sqlite':
        with sqlite3.connect(project / '.yard/data/api.db') as connection:
            connection.execute(statement)
    else:
        subprocess.run(['docker','exec','yard-' + settings['identity'],'psql','-h','127.0.0.1',
                        '-U','yard','-d','yard_api','-v','ON_ERROR_STOP=1','-c',statement], check=True, capture_output=True)

foreign = '20991231000000_ForeignHistory'
try:
    run('database-up', 'db', 'up')
    sql('INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES (\'' + foreign + '\',\'10.0.13\')')
    try:
        run('foreign-history-rejected', 'db', 'migrate', '--all', expected=1)
        if 'manager' in manifest['backends']:
            assert 'Completed database targets: manager' in (case / 'foreign-history-rejected.log').read_text()
    finally:
        sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\'' + foreign + '\'')
    run('history-restored', 'db', 'migrate', '--all')
    run('generate-recovery-probe', 'db', 'generate', 'RecoveryProbe')
    files = list((project / 'apps/api').rglob('*_RecoveryProbe.cs'))
    assert len(files) == 2, 'Both provider migration chains must be generated'
    for file in files:
        source = file.read_text(encoding='utf-8-sig')
        source, count = re.subn(r'(protected override void Up\(MigrationBuilder migrationBuilder\)\s*\{)',
                               r'\1\n            migrationBuilder.Sql("YARD DELIBERATE SQL FAILURE");', source)
        assert count == 1
        file.write_text(source)
    run('sql-failure-stops-order', 'db', 'migrate', '--all', expected=1)
    run('failed-migration-remains-pending', 'db', 'status')
    status = (case / 'failed-migration-remains-pending.log').read_text()
    status = json.loads(next(line for line in status.splitlines() if line.startswith('{')))
    assert any('RecoveryProbe' in x for x in status['pending'])
    assert not any('RecoveryProbe' in x for x in status['applied'])
    if 'manager' in manifest['backends']:
        assert 'Completed database targets: manager' in (case / 'sql-failure-stops-order.log').read_text()
    for file in files:
        file.write_text(file.read_text().replace('YARD DELIBERATE SQL FAILURE', 'SELECT 1'))
    run('retry-after-fix', 'db', 'migrate', '--all')
    run('repeat-success', 'db', 'migrate', '--all')
    run('check-provider-models', 'db', 'check', '--all')
    completed = True
finally:
    subprocess.run([str(tool), 'db', 'down'], cwd=project, capture_output=True, timeout=45)
    (case / 'results.json').write_text(json.dumps({'database':manifest['database'], 'project':str(project),
        'checks':checks, 'passed':completed and all(x['passed'] for x in checks)},indent=2)+'\n')
    print('Migration acceptance record: ' + str(case / 'results.json'), flush=True)
