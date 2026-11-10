#!/usr/bin/env python3
"""Verify frontend ownership and additions using an installed Yard package."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tool', type=Path, required=True)
    tool = parser.parse_args().tool.resolve()
    case = ROOT / '.temp/frontend-acceptance' / uuid.uuid4().hex[:8]
    project = case / 'project'
    (case / 'logs').mkdir(parents=True)
    checks = []
    success = False

    def record(name, passed):
        checks.append({'name': name, 'passed': bool(passed)})
        print(name + (': PASS' if passed else ': FAIL'), flush=True)
        assert passed, name

    def run(name, command, cwd=project, expected=0, env=None):
        result = subprocess.run([str(x) for x in command], cwd=cwd, env=env,
                                capture_output=True, text=True, timeout=600)
        (case / 'logs' / (name + '.log')).write_text(result.stdout + '\nSTDERR:\n' + result.stderr)
        record(name, result.returncode == expected)
        return result.stdout + result.stderr

    def yard(name, *command, **kwargs):
        return run(name, [tool, *command], **kwargs)

    def fingerprints():
        return {str(p.relative_to(project)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in project.rglob('*') if p.is_file()
                and not {'node_modules', 'bin', 'obj', 'dist'}.intersection(p.relative_to(project).parts)}

    try:
        yard('new', 'new', 'FrontendAcceptance', '--database', 'sqlite', '-o', project, cwd=ROOT)
        yard('init', 'init')
        run('evolve-web', ['pnpm', '--dir', 'apps/web', 'add', 'clsx@2.1.1', '--offline', '--ignore-scripts'])
        run('frozen-before-add', ['pnpm', 'install', '--frozen-lockfile', '--offline', '--ignore-scripts'])
        web = project / 'apps/web/package.json'
        original_web = web.read_bytes()
        yard('add-admin', 'add', 'frontend', 'admin')
        record('web-dependencies-preserved', web.read_bytes() == original_web)
        record('admin-template-dependencies', 'clsx' not in json.loads((project / 'apps/admin/package.json').read_text())['dependencies'])
        run('frozen-after-add', ['pnpm', 'install', '--frozen-lockfile', '--offline', '--ignore-scripts'])
        yard('add-portal', 'add', 'frontend', 'portal')
        run('frozen-after-second-add', ['pnpm', 'install', '--frozen-lockfile', '--offline', '--ignore-scripts'])
        for path in [project / 'package.json', *project.glob('apps/*/package.json')]:
            package = json.loads(path.read_text())
            package['scripts'] = {verb: 'node -e "require(\'fs\').writeFileSync(\'script-was-run\',\'yes\')"'
                                  for verb in ['dev', 'build', 'typecheck', 'lint', 'test']}
            path.write_text(json.dumps(package, indent=2) + '\n')
        yard('doctor-with-custom-scripts', 'doctor')
        source = project / 'apps/web/src/review-type-error.ts'
        source.write_text('export const invalid: string = 123;\n')
        typed = yard('typecheck-detects-error', 'typecheck', '--app', 'web', expected=2)
        built = yard('build-detects-error', 'build', '--app', 'web', expected=2)
        record('real-typescript-diagnostics', 'TS2322' in typed and 'TS2322' in built)
        source.unlink()
        source = project / 'apps/web/src/review-lint-error.ts'
        source.write_text('const unused = 123;\n')
        record('real-eslint-diagnostics', 'no-unused-vars' in yard('lint-detects-error', 'lint', '--app', 'web', expected=1))
        source.unlink()
        source = project / 'apps/web/src/review-failure.test.ts'
        source.write_text("import { expect, it } from 'vitest';\nit('review deliberate failure', () => expect(1).toBe(2));\n")
        record('real-vitest-diagnostics', 'review deliberate failure' in yard('test-detects-failure', 'test', '--app', 'web', expected=1))
        source.unlink()
        yard('valid-typecheck', 'typecheck', '--app', 'web')
        yard('valid-tests', 'test', '--app', 'web')
        yard('valid-build', 'build', '--app', 'web')
        record('real-build-artifacts', (project / 'apps/web/dist/index.html').is_file()
               and bool(list((project / 'apps/web/dist/assets').glob('*.js'))))
        record('custom-scripts-not-executed', not list(project.glob('script-was-run'))
               and not list(project.glob('apps/*/script-was-run')))
        manifest = project / 'package.json'
        original = manifest.read_bytes()
        package = json.loads(original)
        package['devDependencies']['typescript'] = '0.0.1'
        manifest.write_text(json.dumps(package))
        record('tool-version-diagnostic', 'typescript' in yard('doctor-rejects-version-drift', 'doctor', expected=1))
        manifest.write_bytes(original)
        web_original = web.read_bytes()
        package = json.loads(web_original)
        package['devDependencies'] = {'@vitejs/plugin-react': '0.0.1'}
        web.write_text(json.dumps(package))
        record('workspace-tool-shadow-diagnostic', 'cannot override' in yard('doctor-rejects-workspace-tool-shadow', 'doctor', expected=1))
        web.write_bytes(web_original)
        config = project / 'apps/web/tsconfig.json'
        original_config = config.read_bytes()
        config.write_text('{"files":[]}\n')
        yard('doctor-rejects-scope-drift', 'doctor', expected=1)
        yard('typecheck-rejects-scope-drift', 'typecheck', '--app', 'web', expected=1)
        yard('sync-restores-checks', 'sync')
        record('typescript-config-restored', config.read_bytes() == original_config)
        record('sync-preserves-business-manifest', manifest.read_bytes() == original)
        lock = project / 'pnpm-lock.yaml'
        original_lock = lock.read_bytes()
        lock.write_text(lock.read_text().replace('apps/web:', 'apps/renamed:'))
        before = fingerprints()
        yard('failed-add', 'add', 'frontend', 'broken', expected=1, env=dict(os.environ, YARD_LOCAL_EXECUTION='1'))
        record('failed-add-preserves-files', before == fingerprints())
        record('failed-add-leaves-no-application', not (project / 'apps/broken').exists())
        lock.write_bytes(original_lock)
        yard('final-doctor', 'doctor')
        success = True
    finally:
        (case / 'results.json').write_text(json.dumps({'passed': success, 'checks': checks}, indent=2) + '\n')
        print('Acceptance record: ' + str(case / 'results.json'), flush=True)


if __name__ == '__main__':
    main()
