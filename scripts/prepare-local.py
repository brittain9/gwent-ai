#!/usr/bin/env python3
"""Install the build into your client and prepare the local runtime data."""
import ast
from pathlib import Path
import shutil
import subprocess
import zipfile

root = Path(__file__).resolve().parents[1]
client = root.parent / 'client'
runtime = root / '.runtime'
if not (client / 'Gwent.exe').is_file():
    raise SystemExit('Extract your original Gwent client into ../client first (see the install guide).')
loader = root / '.local/downloads/MelonLoader.x64.zip'
needed = [loader, root / '.venv/bin/python', root / 'dist/GwentBetaRestorationMod.dll',
          root / 'dist/GwentAI.dll', root / 'dist/GalaxyCommunication.exe']
if missing := [str(path.relative_to(root)) for path in needed if not path.exists()]:
    raise SystemExit(f'Missing {", ".join(missing)}: run scripts/setup.sh --play first.')
with zipfile.ZipFile(loader) as archive:
    for entry in archive.infolist():
        if not (client / entry.filename).resolve().is_relative_to(client.resolve()):
            raise ValueError(f'Unsafe archive path: {entry.filename}')
    archive.extractall(client)
(client / 'Mods').mkdir(exist_ok=True)
for mod in ['GwentBetaRestorationMod.dll', 'GwentAI.dll']:
    shutil.copy2(root / 'dist' / mod, client / 'Mods' / mod)
# Practice opponents and the AI's deck knowledge (scripts/roster.py).
subprocess.run([str(root / 'scripts/roster.py'), 'install'], check=True)
for filename in ['config.json', 'Launch.cfg']:
    shutil.copy2(root / 'server/settings' / filename, client / 'Gwent_Data/StreamingAssets/Settings' / filename)
for directory in ['home', 'logs', 'certs']:
    (runtime / directory).mkdir(parents=True, exist_ok=True)
# Use the existing source's authoritative host list without executing its launcher.
tree = ast.parse((root / 'server/linux_launcher.py').read_text())
hosts = next(ast.literal_eval(node.value) for node in tree.body
             if isinstance(node, ast.Assign)
             and any(isinstance(target, ast.Name) and target.id == 'GOG_HOSTS' for target in node.targets))
(runtime / 'hosts').write_text('127.0.0.1 localhost ' + ' '.join(hosts) + '\n::1 localhost\n')
(runtime / 'resolv.conf').write_text('nameserver 127.0.0.1\noptions timeout:1 attempts:1\n')
if not (runtime / 'certs/fake.key').exists():
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                    '-keyout', str(runtime / 'certs/fake.key'), '-out', str(runtime / 'certs/fake.crt'),
                    '-days', '365', '-subj', '/CN=Local Gwent', '-addext',
                    'subjectAltName=DNS:*.gog.com,DNS:localhost,IP:127.0.0.1'], check=True)
(runtime / 'certs/fake.key').chmod(0o600)
subprocess.run([str(root / '.venv/bin/python'), str(root / 'server/deploy/setup_local_server.py'),
                str(client), '--data-dir', str(runtime / 'server-data')], check=True)
print('Local client and runtime prepared. Run scripts/run-local.sh.')
