#!/usr/bin/env python3
"""Build the five local development-tool packages; never publish remotely."""
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET
root = Path(__file__).resolve().parents[1]
version = ET.parse(root / 'Directory.Build.props').findtext('.//Version')
feed = root / '.temp/feed'
feed.mkdir(parents=True, exist_ok=True)
for old in feed.glob('*.nupkg'):
    old.unlink()
for name in ['Core', 'DotNet', 'DevHost', 'Templates', 'Cli']:
    subprocess.run(['dotnet', 'pack', f'src/Yard.{name}/Yard.{name}.csproj', '-o', str(feed), '-c', 'Release'], cwd=root, check=True)
expected = {f'Iugam.Yard.{name}.{version}.nupkg' for name in ['Core','DotNet','DevHost','Templates','Cli']}
assert {p.name for p in feed.glob('*.nupkg')} == expected
print('Packages:', feed)
