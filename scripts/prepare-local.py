#!/usr/bin/env python3
"""Install the build into your client and prepare the local runtime data."""
from pathlib import Path
import hashlib
import runpy
import shutil
import subprocess
import sys
import zipfile

root = Path(__file__).resolve().parents[1]
client = root.parent / 'client'
runtime = root / '.runtime'
if not (client / 'Gwent.exe').is_file():
    raise SystemExit('Extract your original Gwent client into ../client first (see the install guide).')
loader = root / '.local/downloads/MelonLoader.x64.zip'
needed = [loader, root / '.venv/bin/python', root / 'dist/GwentBetaRestorationMod.dll',
          root / 'dist/GwentAI.dll', root / 'dist/GalaxyCommunication.exe',
          *(root / '.local/runtime-cards' / name for name in ('roles.json', 'answer-params.json', 'manifest.json'))]
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
# Companion assemblies of the mod (dist/GwentAI.*.dll) are installed beside it
# when a build has them; a player's download has none.
companions = sorted(p for p in (root / 'dist').glob('GwentAI.*.dll') if p.is_file())
for companion in companions:
    shutil.copy2(companion, client / 'Mods' / companion.name)
if companions:
    for mod in ['GwentAI.dll', *(p.name for p in companions)]:
        print(f'{mod} md5 {hashlib.md5((client / "Mods" / mod).read_bytes()).hexdigest()}')
cards = client / 'UserData/GwentAI/cards'
cards.mkdir(parents=True, exist_ok=True)
for name in ['roles.json', 'answer-params.json', 'manifest.json']:
    shutil.copy2(root / '.local/runtime-cards' / name, cards / name)
# Practice opponents and the AI's deck knowledge (scripts/roster.py).
subprocess.run([sys.executable, str(root / 'scripts/roster.py'), 'install'], check=True)
for filename in ['config.json', 'Launch.cfg']:
    shutil.copy2(root / 'server/settings' / filename, client / 'Gwent_Data/StreamingAssets/Settings' / filename)
for directory in ['home', 'logs', 'certs']:
    (runtime / directory).mkdir(parents=True, exist_ok=True)
hosts = runpy.run_path(str(root / 'server/gog_hosts.py'))['GOG_HOSTS']
(runtime / 'hosts').write_text('127.0.0.1 localhost ' + ' '.join(hosts) + '\n::1 localhost\n')
(runtime / 'resolv.conf').write_text('nameserver 127.0.0.1\noptions timeout:1 attempts:1\n')
if not (runtime / 'certs/fake.key').exists():
    subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                    '-keyout', str(runtime / 'certs/fake.key'), '-out', str(runtime / 'certs/fake.crt'),
                    '-days', '365', '-subj', '/CN=Local Gwent', '-addext',
                    'subjectAltName=DNS:*.gog.com,DNS:localhost,IP:127.0.0.1'], check=True)
(runtime / 'certs/fake.key').chmod(0o600)
subprocess.run([str(root / '.venv/bin/python'), str(root / 'server/deploy/setup_local_server.py'),
                str(client), '--data-dir', str(runtime / 'server-data'), '--managed'], check=True)
# nginx's static responses, made from the client (scripts/local_session.py
# refreshes them at every session).
subprocess.run([sys.executable, str(root / 'scripts/server_static.py'), '--client', str(client),
                '--out', str(runtime / 'nginx-html')], check=True)
print('Local client and runtime prepared. Run scripts/run-local.sh.')
