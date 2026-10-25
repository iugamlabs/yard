#!/usr/bin/env python3
"""Exercise the installed Yard 0.2 package in isolated repositories."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sqlite3
import subprocess
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]

class Acceptance:
    def __init__(self, args):
        self.args = args
        self.tool = args.tool.resolve()
        self.root = ROOT / '.temp/acceptance' / (args.database + '-' + uuid.uuid4().hex[:8])
        self.app = self.root / 'project'
        (self.root / 'logs').mkdir(parents=True)
        self.checks = []
        self.processes = []
        self.streams = []
        self.success = False

    def record(self, name, passed):
        self.checks.append({'name': name, 'passed': bool(passed)})
        print(name + (': PASS' if passed else ': FAIL'), flush=True)
        assert passed, name

    def run(self, name, command, cwd=None, expected=0, env=None, input=None, timeout=600):
        result = subprocess.run([str(x) for x in command], cwd=cwd or self.app,
            capture_output=True, text=True, input=input, env=env, timeout=timeout)
        (self.root / 'logs' / (name + '.log')).write_text(result.stdout + '\nSTDERR:\n' + result.stderr)
        self.record(name, result.returncode == expected)
        return result

    def yard(self, name, *args, **kwargs):
        return self.run(name, [self.tool, *args], **kwargs)

    def sql(self, statement, app='api'):
        if self.args.database == 'sqlite':
            with sqlite3.connect(self.app / '.yard/data' / (app + '.db')) as db:
                rows = db.execute(statement).fetchall()
            return str(rows)
        state = json.loads((self.app / '.yard/settings.json').read_text())
        result = subprocess.run(['docker','exec','yard-' + state['identity'],'psql','-X','-v','ON_ERROR_STOP=1','-U','yard','-d','yard_' + app.replace('-','_'),'-tAc',statement], capture_output=True, text=True, check=True)
        return result.stdout

    @staticmethod
    def port():
        with socket.socket() as sock:
            sock.bind(('127.0.0.1',0))
            return sock.getsockname()[1]

    def start(self, name, command, cwd, env):
        output = (self.root/'logs'/(name+'.log')).open('w')
        self.streams.append(output)
        process = subprocess.Popen([str(x) for x in command], cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        self.processes.append(process)
        return process

    def ready(self, url, process):
        for _ in range(240):
            if process.poll() is not None:
                raise RuntimeError('Host exited before readiness; inspect logs')
            try:
                if urllib.request.urlopen(url+'/health/ready',timeout=1).status == 200:
                    return
            except Exception:
                pass
            time.sleep(.5)
        raise TimeoutError(url)

    def fingerprints(self):
        result={}
        for pattern in ['*.cs','*.ts','*.tsx','packages.lock.json','pnpm-lock.yaml']:
            for file in self.app.rglob(pattern):
                relative=file.relative_to(self.app)
                if not {'.temp','.yard','bin','obj','node_modules'}.intersection(relative.parts):
                    result[str(relative)]=hashlib.sha256(file.read_bytes()).hexdigest()
        return result

    def empty_contract(self):
        hosting=self.app/'apps/api/src/AcceptanceApp.Api/Hosting/ApplicationHosting.cs'
        files=[hosting,self.app/'packages/api-client/openapi.json',self.app/'packages/api-client/src/generated.ts']
        backups={file:file.read_bytes() for file in files}
        try:
            hosting.write_text(hosting.read_text().replace('app.MapNotes();',''))
            self.yard('empty-contract-export','contracts')
            document=json.loads(files[1].read_text())
            self.record('empty-contract-is-valid',not document.get('paths'))
            self.yard('empty-contract-check','contracts','--check')
        finally:
            for file,content in backups.items():file.write_bytes(content)
        self.yard('restored-contract-check','contracts','--check')

    def native(self):
        native = self.root/'native'
        shutil.copytree(self.app, native, ignore=shutil.ignore_patterns('.yard','.temp','.config','node_modules','bin','obj','dist'))
        self.record('native-copy-has-no-yard-state-or-tool-manifest', not (native/'.yard').exists() and not (native/'.config').exists())
        self.run('native-restore',['dotnet','restore','AcceptanceApp.slnx','--locked-mode','-m:1'],cwd=native)
        self.run('native-build',['dotnet','build','AcceptanceApp.slnx','--no-restore','-m:1'],cwd=native)
        project = native/'apps/api/src/AcceptanceApp.Api/AcceptanceApp.Api.csproj'
        self.run('native-publish',['dotnet','publish',project,'--no-restore','-c','Release','-o',native/'publish'],cwd=native)
        deps = json.loads((native/'publish/AcceptanceApp.Api.deps.json').read_text())
        self.record('production-dependencies-have-no-yard',not any(x.lower().startswith('iugam.yard.') for x in deps['libraries']))
        self.record('published-files-have-no-yard-assemblies',not list((native/'publish').glob('*Yard*.dll')))
        state=json.loads((self.app/'.yard/settings.json').read_text())
        port=self.port();url='http://127.0.0.1:'+str(port)
        env=dict(os.environ, ConnectionStrings__Default=state['connections']['api'], ASPNETCORE_URLS=url)
        process=self.start('native-run',['dotnet',native/'publish/AcceptanceApp.Api.dll'],native,env)
        self.ready(url,process)
        self.record('native-api-runs-with-standard-configuration',urllib.request.urlopen(url+'/api/notes/').status==200)
        process.terminate();process.wait(timeout=30)

    def database(self):
        self.yard('db-status','db','status','--all')
        self.yard('db-check','db','check','--all')
        self.yard('db-generate','db','generate','ExtraTables')
        migration=next((self.app/'apps/api/src/AcceptanceApp.Api/Infrastructure/Migrations').glob('*_ExtraTables.cs'))
        text=migration.read_text().replace('protected override void Up(MigrationBuilder migrationBuilder) {','protected override void Up(MigrationBuilder migrationBuilder) {\n            migrationBuilder.Sql("CREATE TABLE RawParent (Id INTEGER PRIMARY KEY); CREATE TABLE RawChild (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES RawParent(Id))");')
        # EF-generated source uses a newline before the brace until yard fmt is applied.
        if text==migration.read_text():
            text=text.replace('protected override void Up(MigrationBuilder migrationBuilder)\n        {','protected override void Up(MigrationBuilder migrationBuilder)\n        {\n            migrationBuilder.Sql("CREATE TABLE RawParent (Id INTEGER PRIMARY KEY); CREATE TABLE RawChild (Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES RawParent(Id))");')
        assert text!=migration.read_text()
        migration.write_text(text)
        history=self.sql('SELECT * FROM "__EFMigrationsHistory"')
        self.record('migration-plan-reports-pending','ExtraTables' in self.yard('db-plan','db','migrate','--plan').stdout)
        sql=self.yard('db-sql','db','sql').stdout
        self.record('sql-stdout-is-pure-script','CREATE TABLE RawParent' in sql and 'Build succeeded' not in sql)
        self.record('previews-preserve-history',history==self.sql('SELECT * FROM "__EFMigrationsHistory"'))
        self.yard('flush-refuses-pending','db','flush','--yes',expected=1)
        self.yard('migrate-all','db','migrate','--all')
        self.sql('INSERT INTO "Notes" ("Text") VALUES (\'Keep until flush\')')
        history=self.sql('SELECT * FROM "__EFMigrationsHistory"')
        self.yard('flush-requires-confirmation','db','flush',expected=2,input='')
        self.yard('flush-all','db','flush','--all','--yes')
        self.record('flush-preserves-history',history==self.sql('SELECT * FROM "__EFMigrationsHistory"'))
        self.record('flush-clears-notes',self.sql('SELECT COUNT(*) FROM "Notes"').strip() in ['0','[(0,)]'])
        self.sql('INSERT INTO "Notes" ("Text") VALUES (\'Sequence restarted\')')
        self.record('flush-resets-identities',self.sql('SELECT "Id" FROM "Notes"').strip() in ['1','[(1,)]'])
        self.sql("INSERT INTO \"__EFMigrationsHistory\" VALUES ('20990101000000_Foreign','10.0.13')")
        for verb in ['migrate','sql','flush']:
            self.yard('unknown-history-'+verb,'db',verb,*(['--yes'] if verb=='flush' else []),expected=1)
        self.sql("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20990101000000_Foreign'")
        first=self.sql('SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" LIMIT 1')
        # Removing the initial history entry leaves a non-contiguous chain.
        if self.args.database=='sqlite':
            with sqlite3.connect(self.app/'.yard/data/api.db') as db:
                first=db.execute('SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1').fetchone()[0]
        else: first=first.strip()
        self.sql('DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId"=\''+first+'\'')
        self.yard('noncontiguous-history','db','migrate',expected=1)
        self.sql('INSERT INTO "__EFMigrationsHistory" VALUES (\''+first+"','10.0.13')")
        model=self.app/'apps/api/src/AcceptanceApp.Api/Modules/Notes/Notes.cs'
        original=model.read_text();model.write_text(original.replace('public string Text {','public string Extra { get; set; } = "";\n    public string Text {',1))
        self.yard('model-drift-check','db','check',expected=1)
        self.yard('model-drift-migrate','db','migrate',expected=1)
        model.write_text(original)
        self.yard('restored-model','db','check')
        if shutil.which('sqlite3') or self.args.database=='postgresql':
            self.record('shell-pipeline-output','42' in self.yard('shell-pipeline','db','shell',input='SELECT 42;\n').stdout)
        if self.args.backends > 1:
            self.sql("INSERT INTO \"__EFMigrationsHistory\" VALUES ('20990101000000_Foreign','10.0.13')",app='worker1')
            failure=self.yard('partial-completion','db','migrate','--all',expected=1)
            self.record('reports-completed-targets','Completed database targets: api' in failure.stderr)
            self.sql("DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20990101000000_Foreign'",app='worker1')
        self.yard('format-fixture','fmt')
        self.yard('contracts-fixture','contracts')

    def execute(self):
        try:
            self.yard('new','new','AcceptanceApp','--database',self.args.database,'-o',self.app,cwd=ROOT)
            self.yard('new-refuses-nonempty','new','AcceptanceApp','-o',self.app,cwd=ROOT,expected=2)
            self.yard('removed-user-command','user','list',expected=2)
            manifest=self.app/'yard.json';original=manifest.read_text();old=json.loads(original);old['schemaVersion']=1;old['toolVersion']='0.1.0';manifest.write_text(json.dumps(old))
            before=manifest.read_bytes();old_result=self.yard('old-schema-refused','init',expected=2)
            self.record('old-schema-explains-incompatibility','schema 2' in old_result.stderr and 'automatic upgrades' in old_result.stderr)
            self.record('old-schema-does-not-write',manifest.read_bytes()==before and not (self.app/'.yard').exists());manifest.write_text(original)
            for i in range(1,self.args.backends): self.yard('add-backend-'+str(i),'add','backend','worker'+str(i))
            for i in range(1,self.args.frontends): self.yard('add-frontend-'+str(i),'add','frontend','admin' if i==1 else 'web'+str(i))
            self.yard('init','init');self.yard('init-idempotent','init');self.yard('doctor','doctor')
            self.record('one-provider-and-no-runtime-framework',all('Iugam.Yard.' not in p.read_text() for p in (self.app/'apps').rglob('*.csproj')))
            selected='Microsoft.EntityFrameworkCore.Sqlite' if self.args.database=='sqlite' else 'Npgsql.EntityFrameworkCore.PostgreSQL'
            other='Npgsql.EntityFrameworkCore.PostgreSQL' if self.args.database=='sqlite' else 'Microsoft.EntityFrameworkCore.Sqlite'
            backends=[p for p in (self.app/'apps').glob('*/src/*/*.csproj')]
            self.record('selected-provider-only',all(selected in p.read_text() and other not in p.read_text() for p in backends))
            self.record('one-migration-chain-per-backend',all(len(list(p.parent.glob('Infrastructure/Migrations/**/*ModelSnapshot.cs')))==1 for p in backends))
            self.record('no-platform-package',not (self.app/'packages/platform').exists())
            self.yard('contracts-check-offline','contracts','--check',env=dict(os.environ,ConnectionStrings__Default='invalid'))
            before=self.fingerprints()
            self.yard('check','check')
            self.record('check-preserves-source-and-lockfiles',before==self.fingerprints())
            self.database()
            self.yard('post-database-check','check')
            self.native()
            if not self.args.skip_browser: self.yard('verify','verify',timeout=1200)
            self.empty_contract()
            self.yard('sync-check','sync','--check')
            self.yard('upgrade-noop','upgrade')
            self.success=True
        finally:
            for process in self.processes:
                if process.poll() is None:
                    process.terminate()
                    try: process.wait(timeout=20)
                    except subprocess.TimeoutExpired: process.kill();process.wait()
            for stream in self.streams:stream.close()
            if self.args.database=='postgresql' and (self.app/'.yard/settings.json').exists():
                state=json.loads((self.app/'.yard/settings.json').read_text());name='yard-'+state['identity']
                subprocess.run(['docker','rm','-f',name],capture_output=True)
                subprocess.run(['docker','volume','rm',name],capture_output=True)
            (self.root/'results.json').write_text(json.dumps({'passed':self.success,'checks':self.checks},indent=2)+'\n')
            print('Acceptance record: '+str(self.root/'results.json'),flush=True)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--tool',type=Path,required=True)
    p.add_argument('--database',choices=['sqlite','postgresql'],default='sqlite')
    p.add_argument('--backends',type=int,default=1)
    p.add_argument('--frontends',type=int,default=1)
    p.add_argument('--multi',action='store_true')
    p.add_argument('--skip-browser',action='store_true')
    args=p.parse_args()
    if args.multi:args.backends=max(2,args.backends);args.frontends=max(2,args.frontends)
    Acceptance(args).execute()

if __name__=='__main__':main()
