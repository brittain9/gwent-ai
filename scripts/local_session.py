#!/usr/bin/env python3
"""Supervise the local backend and game inside run-local.sh's sandbox."""
import argparse
import getpass
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

from netutil import free_port

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / '.runtime'
# The vendored upstream server (server/UPSTREAM.md) and its database.
SERVER = ROOT / 'server'
SERVER_DATA = RUNTIME / 'server-data'
CLIENT = ROOT.parent / 'client'
PROCESSES = []
LOGS = []
LOG_DIR = RUNTIME / "logs"
# Practice's search workers: headless hosts (GwentHost --search-worker) the
# mod's search runs on in parallel (GwentAI/Practice/WorkerSearch.cs). Each is
# restarted when it dies, up to this many times.
WORKERS = []
WORKER_RESTARTS = 5
HOST = Path(os.environ.get('GWENT_HOST', ROOT / 'dist' / 'host' / 'GwentHost.exe'))
# The same host on .NET (scripts/build-local.sh host builds it where .NET 10
# is installed); workers run on it with GWENT_WORKER_RUNTIME=coreclr
HOST_CORE = Path(os.environ.get('GWENT_HOST_CORE', ROOT / 'dist' / 'host-core' / 'GwentHost.Core.dll'))
LOADER = ROOT / '.local' / 'melonloader' / 'MelonLoader'
MACOS = sys.platform == 'darwin'
MONO = str(ROOT / '.local' / 'mono-x86_64' / 'bin' / 'mono') if MACOS else 'mono'
# The Wine installation run-local.sh chose (on Linux, mounted at /opt/gwent-wine).
WINE_ROOT = Path(os.environ.get('GWENT_WINE_ROOT', '/nonexistent'))
# The client reaches every GOG host on 443. macOS reserves loopback ports
# below 1024 for root, so there nginx listens on 10443 and Wine's connections
# are routed to it (platform/macos/session_net.c).
HTTPS_PORT = 10443 if MACOS else 443
# DXVK translates the client's Direct3D 11 to Vulkan (MoltenVK on macOS).
# DXVK-macOS runs on Wine's own dxgi.
DXVK_DIR = ROOT / ('.local/dxvk-macos' if MACOS else '.local/dxvk')
DXVK_DLLS = ['d3d11', 'd3d10core'] if MACOS else ['d3d11', 'dxgi', 'd3d10core', 'd3d9']
# The session's listeners, all on 127.0.0.1: nginx, the relay, the backend
# and its internal listeners; then the Galaxy service, which starts later.
BACKEND_PORTS = [HTTPS_PORT, 7777, 8443, 8444, 8445, 8446, 8447]
GALAXY_PORT = 9977


def wine_binary(*names):
    """The first of names under the Wine root: Fedora's packages name the
    64-bit binaries wine64 and wineserver64; Debian keeps some in lib/wine."""
    for name in names:
        for directory in ('bin', 'lib/wine', 'lib64/wine'):
            if (WINE_ROOT / directory / name).is_file():
                return WINE_ROOT / directory / name
    raise SystemExit(f'No {names[0]} under the Wine installation at {WINE_ROOT}; see the install guide')


def nginx_binary():
    """nginx built into .local by build-local.sh, else the distribution's."""
    for candidate in (ROOT / '.local/nginx/sbin/nginx', shutil.which('nginx'), '/usr/sbin/nginx'):
        if candidate and Path(candidate).is_file():
            return str(candidate)
    raise SystemExit('No nginx; install your distribution nginx package')


def start(name, command, env, cwd=ROOT):
    log = (LOG_DIR / f'{name}.log').open('w')
    LOGS.append(log)
    process = subprocess.Popen(command, cwd=cwd, env=env, stdout=log,
                               stderr=subprocess.STDOUT)
    PROCESSES.append(process)
    return process


def default_workers():
    """Search workers for Practice: most cores, two kept for the game and the
    desktop, six at most (GWENT_PRACTICE_WORKERS overrides; 0 disables)."""
    return max(0, min(6, (os.cpu_count() or 2) - 2))


def md5(path):
    import hashlib
    return hashlib.md5(path.read_bytes()).hexdigest() if path.is_file() else None


def worker_runtime(choice, host=HOST, host_core=HOST_CORE, dotnet=None, macos=MACOS):
    """The selected worker runtime; auto chooses a matching .NET build or Mono. .NET runs our search about 1.2x faster per
    worker on a P-core at half the memory. It must carry the
    same GwentAI.dll as the Mono host (a host rebuilt alone must not leave the
    workers on an older search). Both play identical games."""
    if choice in (None, 'mono'):
        return 'mono', 'default' if choice is None else 'GWENT_WORKER_RUNTIME=mono'
    automatic = choice == 'auto'
    if choice not in ('auto', 'coreclr'):
        raise SystemExit(f'GWENT_WORKER_RUNTIME={choice}: auto, mono or coreclr')
    dotnet = dotnet if dotnet is not None else shutil.which('dotnet')
    mod = md5(host.parent / 'GwentAI.dll')
    if macos:
        problem = 'Linux only'
    elif not dotnet:
        problem = 'no dotnet'
    elif not host_core.is_file():
        problem = f'no {host_core}'
    elif mod is None or md5(host_core.parent / 'GwentAI.dll') != mod:
        problem = f'{host_core.parent} holds another GwentAI.dll than {host.parent}; run scripts/build-local.sh host'
    else:
        return 'coreclr', 'GWENT_WORKER_RUNTIME=coreclr; .NET build matches the host'
    if automatic:
        return 'mono', 'matching .NET workers unavailable; using Mono'
    raise SystemExit(f'GWENT_WORKER_RUNTIME=coreclr: {problem}')


class Worker:
    """One search worker process on a fixed localhost port, with its log in
    .runtime/logs/worker-N.log. The game reaches it through
    GWENT_SEARCH_WORKERS. Memory: the worker exits above --memory-limit (RSS,
    GWENT_WORKER_MEMORY_MB, default 2048) and is started again."""

    def __init__(self, index, env, log_dir, runtime):
        self.index = index
        self.port = free_port()
        self.runtime = runtime
        # The young generation is the lever on both runtimes: a simulation's
        # copy of the game dies within milliseconds.
        self.env = dict(env, MONO_GC_PARAMS=os.environ.get('GWENT_WORKER_GC_PARAMS', 'soft-heap-limit=819m,nursery-size=256m'))
        self.env.setdefault('DOTNET_GCgen0size', '0x10000000')
        self.restarts = 0
        self.log = (log_dir / f'worker-{index}.log').open('a')
        LOGS.append(self.log)
        self.process = self.start()

    def start(self):
        memory_mb = os.environ.get('GWENT_WORKER_MEMORY_MB', '2048')
        runtime = ['dotnet', str(HOST_CORE)] if self.runtime == 'coreclr' else [MONO, '-O=-float32', str(HOST)]
        return subprocess.Popen(
            [*runtime, '--client', str(CLIENT), '--loader', str(LOADER),
             '--search-worker', '--port', str(self.port), '--memory-limit', memory_mb],
            cwd=ROOT, env=self.env, stdout=self.log, stderr=subprocess.STDOUT)

    def check(self):
        """Starts the worker again if it died (the mod falls back to its own
        search meanwhile and reconnects at its next decision)."""
        if self.process.poll() is not None and self.restarts < WORKER_RESTARTS:
            self.restarts += 1
            print(f'Search worker {self.index} exited with {self.process.returncode}; starting it again '
                  f'({self.restarts}/{WORKER_RESTARTS})', flush=True)
            self.process = self.start()


def start_workers(count, env, log_dir):
    """Starts the search workers and returns the endpoints for the game."""
    if not HOST.is_file():
        print(f'No headless host at {HOST}; Practice searches in the game process alone', flush=True)
        return None
    runtime, why = worker_runtime(os.environ.get('GWENT_WORKER_RUNTIME') or None)
    print(f'Search workers on {runtime} ({why})', flush=True)
    for index in range(count):
        WORKERS.append(Worker(index, env, log_dir, runtime))
    deadline = time.monotonic() + 120
    for worker in WORKERS:
        while True:
            try:
                with socket.create_connection(('127.0.0.1', worker.port), timeout=.5):
                    break
            except OSError:
                if worker.process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f'Search worker {worker.index} did not start; see {log_dir}/worker-{worker.index}.log')
                time.sleep(.2)
    endpoints = ','.join(f'127.0.0.1:{worker.port}' for worker in WORKERS)
    print(f'{count} search workers ready ({endpoints})', flush=True)
    return endpoints


def wine_path(path):
    return 'Z:' + str(path).replace('/', '\\')


def check_ports_free():
    """On macOS the session shares the host's loopback, where a port another
    program holds would answer in place of the backend."""
    for port in [*BACKEND_PORTS, GALAXY_PORT]:
        with socket.socket() as probe:
            # As the backend binds: a closed session's TIME_WAIT is no conflict.
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                probe.bind(('127.0.0.1', port))
            except OSError:
                sys.exit(f'Port {port} on 127.0.0.1 is in use (lsof -nP -iTCP:{port}); '
                         'quit that program or session first.')


def wait_port(port):
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        for process in PROCESSES:
            if process.poll() is not None:
                raise RuntimeError(f'Component exited with {process.returncode}; see .runtime/logs')
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=.2):
                return
        except OSError:
            time.sleep(.1)
    raise RuntimeError(f'Port {port} did not start; see .runtime/logs')


def request(path, body=None, method=None, host='auth.gog.com'):
    context = ssl.create_default_context(cafile=str(RUNTIME / 'certs/fake.crt'))
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(f'https://127.0.0.1:{HTTPS_PORT}' + path, data=data,
                                 headers={'Host': host, 'Content-Type': 'application/json'},
                                 method=method)
    with urllib.request.urlopen(req, context=context, timeout=10) as response:
        payload = response.read()
        return json.loads(payload) if payload else None


def smoke():
    name = f'BuildSmoke{time.time_ns()}'
    profile = request('/register', {'username': name, 'full_collection': True})
    uid = profile['id']
    assert request('/login', {'username': name, 'id': uid})['id'] == uid
    try:
        request('/login', {'username': name, 'id': uid + 1})
    except urllib.error.HTTPError as error:
        assert error.code == 404
    else:
        raise AssertionError('Login accepted the wrong profile ID')
    cards = request(f'/users/{uid}/cards')['items']
    decks = request(f'/users/{uid}/decks')['items']
    assert len(cards) > 100 and len(decks) >= 5, (len(cards), len(decks))
    deck = dict(decks[0], name='Build smoke deck', is_current=False)
    created = request(f'/users/{uid}/decks', deck)
    deck_id = created['id']
    updated = dict(created, name='Updated smoke deck')
    request(f'/users/{uid}/decks/{deck_id}', updated, method='PUT')
    saved = request(f'/users/{uid}/decks')['items']
    assert next(d for d in saved if d['id'] == deck_id)['name'] == updated['name']
    request(f'/users/{uid}/decks/{deck_id}', method='DELETE')
    assert len(request(f'/users/{uid}/decks')['items']) == len(decks)
    print(f'PASS: registration, correct/incorrect login, {len(cards)} cards, {len(decks)} starter decks', flush=True)
    print('PASS: deck creation, update, persistence, and deletion', flush=True)
    return profile


def tcp_listeners():
    """Local addresses of the session's listening TCP sockets."""
    if MACOS:
        pids = ','.join(str(process.pid) for process in PROCESSES)
        output = subprocess.run(['lsof', '-nP', '-a', '-p', pids, '-iTCP', '-sTCP:LISTEN', '-Fn'],
                                capture_output=True, text=True).stdout
        return [line[1:].rsplit(':', 1)[0] for line in output.splitlines() if line.startswith('n')]
    loopback = {'0100007F': '127.0.0.1', '00000000000000000000000001000000': '[::1]'}
    return [loopback.get(fields[1].split(':')[0], fields[1])
            for file in ['/proc/net/tcp', '/proc/net/tcp6']
            for fields in map(str.split, Path(file).read_text().splitlines()[1:])
            if fields[3] == '0A']


class Session:
    """The parts of a session that a program extending this one may change
    (run-local.sh runs GWENT_SESSION_PROGRAM, default this file): its own
    options, the game's environment, where the workers log, the game's
    display and the clean-up after the game."""

    def add_arguments(self, parser):
        pass

    def configure(self, args):
        """Runs after the options are parsed, before the defaults are filled."""

    def game_environment(self, args, env):
        """Practice: the games are recorded in data/human-games, the one
        place in the tree that the session writes (GwentAI/Practice/PracticeAI.cs,
        GameRecord)."""
        games = ROOT / 'data' / 'human-games'
        games.mkdir(parents=True, exist_ok=True)
        env['GWENT_GAMES_DIR'] = wine_path(games)
        # The local profile's name: a deck tagged with it (DeckSpec.Owner)
        # is never in the AI's prior when that profile plays.
        env['GWENT_PLAYER'] = args.username.lower()
        if args.stock_ai:
            env['GWENT_STOCK_AI'] = '1'

    def worker_log_dir(self, args):
        return LOG_DIR

    def display_args(self, args):
        if args.windowed:
            return ['-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720']
        return ['-screen-fullscreen', '1', '-window-mode', 'borderless']

    def finish(self, args):
        """Runs after the game and every process of the session stopped."""


def main(session=None):
    global LOG_DIR
    session = session or Session()
    # Closing the terminal (SIGHUP) or a kill (SIGTERM) still runs the cleanup
    # below. On Linux the sandbox also ends with this process; on macOS
    # nothing else would stop the backend and Wine's server.
    for signum in (signal.SIGHUP, signal.SIGTERM):
        signal.signal(signum, lambda signum, frame: sys.exit(128 + signum))
    parser = argparse.ArgumentParser()
    parser.add_argument('--smoke', action='store_true',
                        help='check the local server, the network isolation and Wine, then exit without the game')
    parser.add_argument('--username', default=getpass.getuser(),
                        help='profile name for a new local profile (default: your login name)')
    parser.add_argument('--windowed', action='store_true')
    parser.add_argument('--stock-ai', action='store_true',
                        help="Practice against the built-in AI instead of the fair search")
    parser.add_argument('--workers', type=int,
                        help='search worker processes for the Practice AI (0: search in the game process only; '
                             f'default {default_workers()}, GWENT_PRACTICE_WORKERS)')
    session.add_arguments(parser)
    args = parser.parse_args()
    session.configure(args)
    if args.workers is None:
        args.workers = int(os.environ.get('GWENT_PRACTICE_WORKERS', default_workers()))
    if not os.environ.get('GWENT_SANDBOX'):
        sys.exit('Run through scripts/run-local.sh to isolate networking and hostname changes.')
    env = os.environ.copy()
    env.update(GWENT_BIND_HOST='127.0.0.1', GWENT_SERVER_IP='127.0.0.1',
               GWENT_USE_SQLITE='1', GWENT_CERT_DIR=str(RUNTIME / 'certs'),
               GWENT_DATA_DIR=str(SERVER_DATA), PYTHONUNBUFFERED='1')
    if args.smoke:
        import tempfile
        test_data = Path(tempfile.mkdtemp(prefix='smoke-', dir=RUNTIME))
        (test_data / 'Data_definitions').symlink_to(SERVER_DATA / 'Data_definitions', target_is_directory=True)
        env['GWENT_DATA_DIR'] = str(test_data)
    LOG_DIR = RUNTIME / ('logs-smoke' if args.smoke else 'logs')
    LOG_DIR.mkdir(exist_ok=True)
    nginx_root = RUNTIME / ('nginx-smoke' if args.smoke else 'nginx')
    for directory in ['logs', 'temp', 'conf']:
        (nginx_root / directory).mkdir(parents=True, exist_ok=True)
    # Replace the link when the server tree has moved (a dangling link is not .exists()).
    html = nginx_root / 'html'
    if html.is_symlink() and html.resolve() != (SERVER / 'Nginx/html').resolve():
        html.unlink()
    if not html.exists():
        html.symlink_to(SERVER / 'Nginx/html', target_is_directory=True)
    for filename in ['fake.crt', 'fake.key']:
        shutil.copy2(RUNTIME / 'certs' / filename, nginx_root / 'conf' / filename)
    conf = (SERVER / 'host_nginx.conf').read_text().replace('listen 443 ssl;', f'listen 127.0.0.1:{HTTPS_PORT} ssl;')
    # As the namespace's root on Linux, nginx would otherwise switch to nobody.
    user = 'user root root;\n' if os.geteuid() == 0 else ''
    (nginx_root / 'nginx.conf').write_text('daemon off;\n' + user + conf)
    check_ports_free()
    try:
        for name in ['server', 'broker', 'relay']:
            start(name, [sys.executable, str(SERVER / 'deploy' / f'{name}.py')], env)
        # -e: a distribution nginx would otherwise open its own system log first.
        nginx = [nginx_binary(), '-p', str(nginx_root) + '/', '-c', str(nginx_root / 'nginx.conf'),
                 '-e', str(nginx_root / 'logs/error.log')]
        subprocess.run([*nginx, '-t'], check=True, env=env)
        start('nginx', nginx, env)
        for port in BACKEND_PORTS:
            wait_port(port)
        print('Local backend ready; networking is isolated from the host.', flush=True)
        if args.smoke:
            smoke()
            # Every server listener must be loopback-only, including internal ports.
            listeners = tcp_listeners()
            assert listeners and set(listeners) <= {'127.0.0.1', '[::1]'}, listeners
            print('PASS: all backend TCP listeners are bound to loopback', flush=True)
            try:
                with socket.create_connection(('1.1.1.1', 443), timeout=.2):
                    raise AssertionError('Private network unexpectedly has external connectivity')
            except OSError:
                pass
            print('PASS: blocked external networking for the backend', flush=True)
        else:
            profile_file = RUNTIME / 'profile.json'
            if profile_file.exists():
                profile = json.loads(profile_file.read_text())
                request('/login', {'username': profile['username'], 'id': profile['id']})
            else:
                profile = request('/register', {'username': args.username, 'full_collection': True})
                profile_file.write_text(json.dumps(profile, indent=2) + '\n')
                profile_file.chmod(0o600)
            users_file = RUNTIME / 'users.json'
            users_file.write_text(json.dumps([{'id': profile['id'], 'username': profile['username']}]))
            users_file.chmod(0o600)
            env['GWENT_USERS_FILE'] = str(users_file)
            start('commservice', [sys.executable, str(SERVER / 'commservice.py'), '--standalone'], env)
            if not MACOS:
                # Answers the names in .runtime/hosts for resolv.conf (run-local.sh).
                start('dns', [sys.executable, str(SERVER / 'dns_proxy.py'), '--server-ip', '127.0.0.1',
                              '--upstream', '127.0.0.1'], env)
            wait_port(GALAXY_PORT)
        wine = str(wine_binary('wine64', 'wine'))
        wineserver = str(wine_binary('wineserver64', 'wineserver'))
        # The smoke test boots a throwaway prefix, never one a game may be using.
        prefix = test_data / 'wine' if args.smoke else RUNTIME / 'wine'
        # Wine's server runs in the prefix's directory, which wineboot creates
        # only after the server is up.
        prefix.mkdir(exist_ok=True)
        env.update(WINEPREFIX=str(prefix), WINEDEBUG=os.environ.get('GWENT_WINE_DEBUG', '-all'),
                   WINESERVER=wineserver,
                   WINELOADER=wine,
                   WINEDLLOVERRIDES='version=n,b;mscoree=n,b;' + ','.join(DXVK_DLLS) + '=n')
        if MACOS:
            env.update(DYLD_INSERT_LIBRARIES=str(ROOT / 'dist/session_net.dylib'),
                       GWENT_HOSTS_FILE=str(RUNTIME / 'hosts'), GWENT_HTTPS_PORT=str(HTTPS_PORT))
        else:
            env['LD_LIBRARY_PATH'] = str(WINE_ROOT / 'lib') + ':' + str(WINE_ROOT / 'lib64')
        wine_log = (LOG_DIR / 'wine-setup.log').open('w')
        LOGS.append(wine_log)
        def wine_command(*command, allowed=(0,)):
            result = subprocess.run([wine, *command], env=env, stdout=wine_log,
                                    stderr=subprocess.STDOUT, timeout=90)
            if result.returncode not in allowed:
                raise RuntimeError(f'Wine {command[0]} failed ({result.returncode}); see wine-setup.log')
        if MACOS:
            # Wine's server opens the game's connections, so it must run in
            # the sandbox. Stop one already running for this prefix (started
            # outside the session by winecfg, say), then start the session's
            # own and keep it until the end (finally: -k), so that no outside
            # one can take its place between Wine commands.
            for flag in ['-k', '-w']:
                subprocess.run([wineserver, flag], env=env, stdout=wine_log, stderr=subprocess.STDOUT)
            subprocess.run([wineserver, '--persistent'], env=env, stdout=wine_log, stderr=subprocess.STDOUT,
                           check=True)
        game_overrides = env['WINEDLLOVERRIDES']
        env['WINEDLLOVERRIDES'] = 'mscoree,mshtml,winemenubuilder=d'
        wine_command('wineboot', '-u')
        env['WINEDLLOVERRIDES'] = game_overrides
        if args.smoke:
            # Name routing and containment as the client sees them, through Wine.
            netprobe = ROOT / 'dist/netprobe.exe'
            if not netprobe.is_file():
                raise SystemExit(f'No {netprobe}; run scripts/build-local.sh')
            probe = subprocess.run([wine, str(netprobe)], env=env, capture_output=True, text=True, timeout=90)
            print(probe.stdout, end='', flush=True)
            if probe.returncode:
                raise AssertionError(f'Wine networking checks failed: {probe.stderr.strip()}')
            return
        # Proton installs these native DXVK DLLs when it manages a prefix.
        # We launch Wine directly, so install its existing runtime DLLs explicitly.
        for arch, directory in [('x86_64-windows', 'system32'), ('i386-windows', 'syswow64')]:
            for name in DXVK_DLLS:
                shutil.copy2(DXVK_DIR / arch / f'{name}.dll',
                             RUNTIME / 'wine/drive_c/windows' / directory / f'{name}.dll')
        if MACOS:
            # Without it, Wine reports a Retina display at half its pixel size
            # and macOS scales the game up 2x, blurred.
            wine_command('reg', 'add', r'HKCU\Software\Wine\Mac Driver', '/v', 'RetinaMode', '/d', 'y', '/f')
        service_dir = RUNTIME / 'wine/drive_c/ProgramData/GOG.com/Galaxy/redists'
        service_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(ROOT / 'dist/GalaxyCommunication.exe', service_dir / 'GalaxyCommunication.exe')
        wine_command(r'C:\windows\system32\sc.exe', 'create', 'GalaxyCommunication',
                     r'binpath=C:\ProgramData\GOG.com\Galaxy\redists\GalaxyCommunication.exe',
                     'start=', 'auto', allowed=(0, 49))
        wine_command(r'C:\windows\system32\sc.exe', 'start', 'GalaxyCommunication', allowed=(0, 32))
        session.game_environment(args, env)
        if env.get('GWENT_AI_CARD_DATA'):
            env['GWENT_AI_CARD_DATA'] = wine_path(Path(env['GWENT_AI_CARD_DATA']))
        if args.workers > 0 and not args.stock_ai:
            endpoints = start_workers(args.workers, os.environ.copy(), session.worker_log_dir(args))
            if endpoints:
                env['GWENT_SEARCH_WORKERS'] = endpoints
        game = start('game', [wine, str(CLIENT / 'Gwent.exe'), *session.display_args(args)], env, CLIENT)
        print(f'Launching Gwent as {profile["username"]}. Logs: {RUNTIME / "logs"}', flush=True)
        while game.poll() is None:
            for process in PROCESSES[:-1]:
                if process.poll() is not None:
                    raise RuntimeError('A backend component stopped; see .runtime/logs')
            for worker in WORKERS:
                worker.check()
            time.sleep(1)
        if game.returncode:
            raise RuntimeError(f'Game exited with {game.returncode}; see game.log')
    finally:
        if 'wineserver' in locals():
            try:
                subprocess.run([wineserver, '-k'], env=env,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               timeout=10)
            except subprocess.TimeoutExpired:
                print('Wine did not stop within 10 s; stopping the backend anyway', file=sys.stderr)
        for process in reversed(PROCESSES + [worker.process for worker in WORKERS]):
            if process.poll() is None:
                process.terminate()
        for process in PROCESSES + [worker.process for worker in WORKERS]:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        for log in LOGS:
            log.close()
        session.finish(args)
        if args.smoke:
            shutil.rmtree(test_data)


if __name__ == '__main__':
    main()
