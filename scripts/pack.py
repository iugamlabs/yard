#!/usr/bin/env python3
from pathlib import Path
import subprocess
root=Path(__file__).resolve().parents[1]
feed=root/'.temp/feed';feed.mkdir(parents=True,exist_ok=True)
def run(*args):subprocess.run(args,cwd=root,check=True)
for name in ['Core','AspNetCore','DevHost']:
 run('dotnet','pack',f'src/Yard.{name}/Yard.{name}.csproj','-o',str(feed),'-c','Release')
# Stamp lockfile hashes with the exact runtime artifacts shipped in this tool release.
import json,hashlib,base64
for package in feed.glob('*.nupkg'):
 package_id=package.name.removesuffix('.0.1.0.nupkg')
 digest=base64.b64encode(hashlib.sha512(package.read_bytes()).digest()).decode()
 for lockfile in (root/'templates/monorepo').rglob('packages.lock.json'):
  data=json.loads(lockfile.read_text())
  for framework in data['dependencies'].values():
   if package_id in framework:framework[package_id]['contentHash']=digest
  lockfile.write_text(json.dumps(data,indent=2)+'\n')
run('dotnet','pack','src/Yard.Templates/Yard.Templates.csproj','-o',str(feed),'-c','Release')
# Do not include a previous CLI package recursively inside the next CLI.
for old in feed.glob('Iugam.Yard.Cli.*.nupkg'):old.unlink()
run('dotnet','pack','src/Yard.Cli/Yard.Cli.csproj','-o',str(feed),'-c','Release')
print('Packages:',feed)
