#!/usr/bin/env python3
"""Installed-tool checkout, data, port and Ctrl+C acceptance (POSIX host)."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--tool', type=Path, required=True)
args = p.parse_args()
tool = args.tool.resolve()
case = ROOT / '.temp/process-acceptance' / uuid.uuid4().hex[:8]
case.mkdir(parents=True)
checks = []
hosts = []
streams = []
completed = False

def record(name, condition):
    checks.append({'name': name, 'passed': bool(condition)})
    print(name + (': PASS' if condition else ': FAIL'), flush=True)
    assert condition, name

def run(name, app, *argv, input=None, expected=0, env=None):
    with (case / (name + '.log')).open('w') as output:
        r = subprocess.run([str(tool), *argv], cwd=app, input=input, text=True,
                           stdout=output, stderr=subprocess.STDOUT, timeout=300, env=env)
    record(name, r.returncode == expected)

def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]

def closed(number):
    with socket.socket() as s:
        return s.connect_ex(('127.0.0.1', number)) != 0

def ready(url, proc):
    for _ in range(180):
        if proc.poll() is not None:
            raise RuntimeError('Host exited before readiness')
        try:
            if urllib.request.urlopen(url + '/health/ready', timeout=1).status == 200:
                return
        except Exception:
            pass
        time.sleep(.5)
    raise TimeoutError(url)

try:
    apps = [case / 'a', case / 'b']
    for i, app in enumerate(apps):
        run('new-' + str(i), ROOT, 'new', 'ParallelProject', '--database', 'sqlite', '--output', str(app))
        run('init-' + str(i), app, 'init')
        package_file = app / 'apps/web/package.json'
        package = json.loads(package_file.read_text())
        package['scripts']['dev'] = 'node -e "process.exit(23)"'
        package_file.write_text(json.dumps(package, indent=2) + '\n')
    settings = [json.loads((a / '.yard/settings.json').read_text()) for a in apps]
    record('separate-identities', settings[0]['identity'] != settings[1]['identity'])
    record('separate-databases', settings[0]['connections']['api'] != settings[1]['connections']['api'])
    with socket.socket() as occupied:
        occupied.bind(('127.0.0.1', 0))
        occupied.listen()
        env = dict(os.environ, YARD_PORT=str(occupied.getsockname()[1]))
        run('port-conflict', apps[0], 'dev', expected=1, env=env)
        record('clear-port-diagnostic', 'already in use' in (case / 'port-conflict.log').read_text())
    gateway_ports = [port(), port()]
    lock_hashes = [hashlib.sha256((a / 'pnpm-lock.yaml').read_bytes()).digest() for a in apps]
    for i, app in enumerate(apps):
        output = (case / ('dev-' + str(i) + '.log')).open('w')
        streams.append(output)
        host = subprocess.Popen([str(tool), 'dev'], cwd=app, stdout=output, stderr=subprocess.STDOUT,
                                env=dict(os.environ, YARD_PORT=str(gateway_ports[i]), DOCKER_HOST='tcp://127.0.0.1:1'), start_new_session=True)
        hosts.append(host)
    urls = ['http://localhost:' + str(x) for x in gateway_ports]
    for url, host in zip(urls, hosts):
        ready(url, host)
    record('parallel-ready', True)
    for i, url in enumerate(urls):
        vite_ready = False
        for _ in range(60):
            if hosts[i].poll() is not None:
                break
            try:
                with urllib.request.urlopen(url + '/@vite/client', timeout=1) as response:
                    vite_ready = response.status == 200 and b'createHotContext' in response.read()
                if vite_ready:
                    break
            except Exception:
                pass
            time.sleep(.25)
        record('vite-ignores-custom-dev-script-' + str(i), vite_ready)
    try:
        status = urllib.request.urlopen(urls[0] + '/admin/', timeout=5).status
    except urllib.error.HTTPError as error:
        status = error.code
    record('undeclared-admin-is-not-a-platform-page', status == 404)

    for i, url in enumerate(urls):
        data = json.dumps({'text': 'Checkout ' + str(i)}).encode()
        request = urllib.request.Request(url + '/api/notes/', data=data, headers={'Content-Type':'application/json'})
        with urllib.request.urlopen(request) as response:
            record('create-note-' + str(i), response.status == 201)
    for i, url in enumerate(urls):
        with urllib.request.urlopen(url + '/api/notes/') as response:
            records = json.load(response)
        record('isolated-data-' + str(i), len(records) == 1 and records[0]['text'] == 'Checkout ' + str(i))
    run('independent-stop', apps[0], 'dev', 'stop')
    with urllib.request.urlopen(urls[1] + '/api/notes/') as response:
        record('independent-stop-preserves-second-checkout', json.load(response)[0]['text'] == 'Checkout 1')
    hosts[0].wait(timeout=30)
    record('second-checkout-survives', hosts[1].poll() is None)
    os.killpg(hosts[1].pid, signal.SIGINT)
    hosts[1].wait(timeout=30)
    record('ctrl-c-exit', hosts[1].returncode in (0, 130))
    time.sleep(1)
    for i, state in enumerate(settings):
        remaining = [n for n in [*state['ports'].values(), state['controlPort'], gateway_ports[i]] if not closed(n)]
        if remaining:
            print('Remaining listeners: ' + str(remaining), flush=True)
        record('no-listeners-' + str(i), not remaining)
        record('unchanged-lock-' + str(i), hashlib.sha256((apps[i] / 'pnpm-lock.yaml').read_bytes()).digest() == lock_hashes[i])
    output = (case / 'selected-api.log').open('w')
    streams.append(output)
    host = subprocess.Popen([str(tool), 'dev', '--app', 'api'], cwd=apps[0], stdout=output, stderr=subprocess.STDOUT,
                            env=dict(os.environ, YARD_PORT=str(gateway_ports[0]), DOCKER_HOST='tcp://127.0.0.1:1'), start_new_session=True)
    hosts.append(host)
    ready(urls[0], host)
    status = subprocess.run([str(tool), 'dev', 'status'], cwd=apps[0], capture_output=True, text=True, check=True, timeout=30)
    names = {x['name'] for x in json.loads(status.stdout)['applications']}
    record('selected-api-excludes-frontend', 'api' in names and 'web' not in names)
    run('selected-api-stop', apps[0], 'dev', 'stop')
    host.wait(timeout=30)
    record('sqlite-with-unavailable-docker', all(closed(n) for n in [*settings[0]['ports'].values(), settings[0]['controlPort'], gateway_ports[0]]))
    completed = True
finally:
    for host in hosts:
        if host.poll() is None:
            os.killpg(host.pid, signal.SIGTERM)
            try:
                host.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(host.pid, signal.SIGKILL)
    for stream in streams:
        stream.close()
    (case / 'results.json').write_text(json.dumps({'checks':checks,'passed':completed and bool(checks) and all(x['passed'] for x in checks)},indent=2)+'\n')
    print('Process acceptance record: ' + str(case / 'results.json'), flush=True)
