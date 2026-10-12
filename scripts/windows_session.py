#!/usr/bin/env python3
"""A Practice session on Windows: the local server, nginx and the Galaxy
stand-in on 127.0.0.1, the search workers, and the client with its GOG names
routed in its own process (platform/windows/session_net.c). The Windows
counterpart of scripts/run-local.sh and scripts/local_session.py; it shares
their parts and changes nothing outside this folder and the client: no
administrator rights, no hosts file, no Windows service, no firewall rule, no
certificate store (the game accepts this install's certificate in its own
process; setup's --trust-cert fallback is off by default, see windows_play.py).

Run it through scripts\\run-local.cmd (scripts/windows_play.py run).
"""

import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time

import local_session as ls
import windows_paths as paths

CREATE_NEW_PROCESS_GROUP = 0x00000200
# The GwentSessionNet plugin's exit code when it cannot route the game
# (platform/windows/SessionNet/SessionNetPlugin.cs, FailedExitCode).
SESSION_NET_FAILED = 0x5E55


def certificate_sha256(path):
    """This install's certificate as the game process accepts it
    (GWENT_SESSION_CERT_SHA256, platform/windows/session_net.c)."""
    import selfsigned
    return selfsigned.sha256(selfsigned.pem_der(Path(path).read_text(encoding="ascii")))


def nginx_conf(template, nginx_root, html, certs, https_port=443):
    """server/host_nginx.conf for Windows: every path absolute (nginx for
    Windows resolves relative ones against its compiled-in prefix) and every
    listener on 127.0.0.1."""
    def quoted(*parts):
        return '"' + Path(*parts).as_posix() + '"'
    replacements = {
        "listen 443 ssl;": f"listen 127.0.0.1:{https_port} ssl;",
        "conf/fake.crt": quoted(certs, "fake.crt"),
        "conf/fake.key": quoted(certs, "fake.key"),
        "error_log  logs/error.log": "error_log  " + quoted(nginx_root, "logs", "error.log"),
        "pid        logs/nginx.pid": "pid        " + quoted(nginx_root, "logs", "nginx.pid"),
        "access_log             logs/access.log": "access_log             " + quoted(nginx_root, "logs", "access.log"),
        "root html/shop;": f"root {quoted(html, 'shop')};",
        "alias html/card_definitions/prices;": f"alias {quoted(html, 'card_definitions', 'prices')};",
        "root html;": f"root {quoted(html)};",
    }
    for name in ("client_body_temp", "proxy_temp", "fastcgi_temp", "uwsgi_temp", "scgi_temp"):
        replacements[f"temp/{name}"] = quoted(nginx_root, "temp", name)
    for old, new in replacements.items():
        if old not in template:
            raise SystemExit(f"server/host_nginx.conf no longer contains {old!r}; update windows_session.nginx_conf")
        template = template.replace(old, new)
    return "daemon off;\n" + template


def session_hosts(path):
    """The names the client routes to the local server: the GOG host list the
    Linux and macOS sessions use, and every other *.gog.com name, so the
    client reaches no GOG service."""
    path.write_text("127.0.0.1 " + " ".join(paths.gog_hosts()) + "\n127.0.0.1 *.gog.com\n",
                    encoding="ascii", newline="\n")
    return path


def ports_free(ports):
    """Ports on 127.0.0.1 another program holds. No SO_REUSEADDR: on Windows
    it would let the probe share a port in use."""
    busy = []
    for port in ports:
        with socket.socket() as probe:
            try:
                probe.bind(("127.0.0.1", port))
            except OSError:
                busy.append(port)
    return busy


def check_ports():
    busy = ports_free([*ls.BACKEND_PORTS, ls.GALAXY_PORT])
    if busy:
        hints = []
        if ls.GALAXY_PORT in busy:
            hints.append(f"port {ls.GALAXY_PORT} is GOG Galaxy's: quit GOG Galaxy")
        if 443 in busy:
            hints.append("port 443: another web server or a VPN client")
        sys.exit(f"Ports in use on 127.0.0.1: {', '.join(map(str, busy))} "
                 f"(netstat -ano -p TCP lists each port's process id). " + "; ".join(hints))


def tcp_listeners(pids):
    """Local addresses ("127.0.0.1", "[::1]", ...) of the listening TCP
    sockets of these processes, from the IP Helper API's owner-pid tables
    (GetExtendedTcpTable): the same on every Windows display language, unlike
    netstat's text."""
    import ctypes
    from ctypes import wintypes

    TCP_TABLE_OWNER_PID_LISTENER = 3
    ERROR_INSUFFICIENT_BUFFER = 122

    class ROW4(ctypes.Structure):
        _fields_ = [("state", wintypes.DWORD), ("local_addr", ctypes.c_ubyte * 4), ("local_port", wintypes.DWORD),
                    ("remote_addr", ctypes.c_ubyte * 4), ("remote_port", wintypes.DWORD), ("pid", wintypes.DWORD)]

    class ROW6(ctypes.Structure):
        _fields_ = [("local_addr", ctypes.c_ubyte * 16), ("local_scope", wintypes.DWORD),
                    ("local_port", wintypes.DWORD), ("remote_addr", ctypes.c_ubyte * 16),
                    ("remote_scope", wintypes.DWORD), ("remote_port", wintypes.DWORD),
                    ("state", wintypes.DWORD), ("pid", wintypes.DWORD)]

    iphlpapi = ctypes.WinDLL("iphlpapi")
    iphlpapi.GetExtendedTcpTable.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL,
                                             wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
    iphlpapi.GetExtendedTcpTable.restype = wintypes.DWORD
    found = []
    for family, row_type in ((socket.AF_INET, ROW4), (socket.AF_INET6, ROW6)):
        size = wintypes.DWORD(0)
        result = ERROR_INSUFFICIENT_BUFFER
        while result == ERROR_INSUFFICIENT_BUFFER:
            buffer = ctypes.create_string_buffer(max(size.value, 4))
            result = iphlpapi.GetExtendedTcpTable(buffer, ctypes.byref(size), False, family,
                                                  TCP_TABLE_OWNER_PID_LISTENER, 0)
        if result:
            raise OSError(f"GetExtendedTcpTable failed: {result}")
        count = wintypes.DWORD.from_buffer(buffer).value
        # The rows follow dwNumEntries at the rows' alignment (4 bytes).
        rows = (row_type * count).from_buffer(buffer, ctypes.sizeof(wintypes.DWORD))
        for row in rows:
            if row.pid in pids:
                address = socket.inet_ntop(family, bytes(row.local_addr))
                found.append(address if family == socket.AF_INET else f"[{address}]")
    return found


def worker_problem(host_core=None, mod=None):
    """Why the search workers cannot run on .NET here, or None. They must
    carry the same GwentAI.dll as the mod in the game."""
    host_core = Path(host_core or ls.HOST_CORE)
    mod = Path(mod or paths.ROOT / "dist" / "GwentAI.dll")
    if not shutil.which("dotnet"):
        return "no dotnet (install the .NET 10 runtime)"
    if not host_core.is_file():
        return f"no {host_core}; run scripts\\setup.cmd"
    if not (host_core.parent / "0Harmony.dll").is_file():
        return f"{host_core.parent} lacks its NuGet packages; run scripts\\setup.cmd"
    if ls.md5(host_core.parent / "GwentAI.dll") != ls.md5(mod):
        return f"{host_core.parent} holds another GwentAI.dll than {mod}; extract the complete release again"
    return None


def start_workers(count, env, log_dir, job):
    problem = worker_problem()
    if problem:
        print(f"Search workers off ({problem}); Practice searches in the game process alone", flush=True)
        return None
    for index in range(count):
        worker = ls.Worker(index, env, log_dir, "coreclr")
        ls.WORKERS.append(worker)
        if job:
            job.add(worker.process.pid)
    deadline = time.monotonic() + 120
    for worker in ls.WORKERS:
        while True:
            try:
                with socket.create_connection(("127.0.0.1", worker.port), timeout=.5):
                    break
            except OSError:
                if worker.process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f"Search worker {worker.index} did not start; see {log_dir}\\worker-{worker.index}.log")
                time.sleep(.2)
    endpoints = ",".join(f"127.0.0.1:{worker.port}" for worker in ls.WORKERS)
    print(f"{count} search worker{'s' if count != 1 else ''} ready on .NET ({endpoints})", flush=True)
    return endpoints


def start(name, command, env, cwd=paths.ROOT):
    log = (ls.LOG_DIR / f"{name}.log").open("w", encoding="utf-8")
    ls.LOGS.append(log)
    process = subprocess.Popen(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=CREATE_NEW_PROCESS_GROUP)
    ls.PROCESSES.append(process)
    return process


def backend(env, nginx_root, html):
    """Starts the server, broker, relay and nginx; waits for their ports."""
    for directory in ("logs", "temp"):
        (nginx_root / directory).mkdir(parents=True, exist_ok=True)
    conf = nginx_root / "nginx.conf"
    conf.write_text(nginx_conf((paths.SERVER / "host_nginx.conf").read_text(encoding="utf-8"),
                               nginx_root, html, paths.CERTS), encoding="utf-8", newline="\n")
    for name in ("server", "broker", "relay"):
        start(name, [sys.executable, str(paths.SERVER / "deploy" / f"{name}.py")], env)
    nginx = [str(paths.NGINX), "-p", nginx_root.as_posix() + "/", "-c", conf.as_posix(),
             "-e", (nginx_root / "logs" / "error.log").as_posix()]
    test = subprocess.run([*nginx, "-t"], env=env, capture_output=True, text=True)
    if test.returncode:
        raise RuntimeError(f"nginx configuration check failed:\n{test.stderr}")
    start("nginx", nginx, env, cwd=nginx_root)
    for port in ls.BACKEND_PORTS:
        ls.wait_port(port)
    print("Local backend ready on 127.0.0.1.", flush=True)


def stop_all():
    for process in reversed(ls.PROCESSES + [worker.process for worker in ls.WORKERS]):
        if process.poll() is None:
            # nginx's worker process outlives its master; /T ends the tree.
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for process in ls.PROCESSES + [worker.process for worker in ls.WORKERS]:
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
    for log in ls.LOGS:
        log.close()


def run(args, job=None, worker_job=None):
    """args: smoke, username, windowed, stock_ai, workers (see windows_play.py)."""
    state = paths.load_state()
    client = Path(state["client"])
    if not (client / "Gwent.exe").is_file():
        sys.exit(f"No Gwent.exe in {client}; run scripts\\setup.cmd --client <your client folder>")
    for needed in (paths.NGINX, paths.CERTS / "fake.crt", paths.SESSION_NET, paths.SERVER_DATA):
        if not needed.exists():
            sys.exit(f"Missing {needed}; run scripts\\setup.cmd")
    ls.CLIENT = client
    env = os.environ.copy()
    env.update(GWENT_BIND_HOST="127.0.0.1", GWENT_SERVER_IP="127.0.0.1", GWENT_USE_SQLITE="1",
               GWENT_CERT_DIR=str(paths.CERTS), GWENT_DATA_DIR=str(paths.SERVER_DATA),
               PYTHONUNBUFFERED="1", PYTHONUTF8="1")
    env.setdefault("GWENT_AI_THINK_MS", "20000")
    test_data = None
    if args.smoke:
        test_data = Path(tempfile.mkdtemp(prefix="smoke-", dir=paths.RUNTIME))
        shutil.copytree(paths.SERVER_DATA / "Data_definitions", test_data / "Data_definitions")
        env["GWENT_DATA_DIR"] = str(test_data)
    ls.LOG_DIR = paths.RUNTIME / ("logs-smoke" if args.smoke else "logs")
    ls.LOG_DIR.mkdir(parents=True, exist_ok=True)
    nginx_root = paths.RUNTIME / ("nginx-smoke" if args.smoke else "nginx")
    import server_static
    server_static.write(client, paths.HTML)
    check_ports()
    try:
        backend(env, nginx_root, paths.HTML)
        if args.smoke:
            ls.smoke()
            pids = {process.pid for process in ls.PROCESSES}
            # nginx serves from its worker process, a child of the one started.
            pids |= child_pids(pids)
            listeners = tcp_listeners(pids)
            assert listeners and set(listeners) <= {"127.0.0.1", "[::1]"}, listeners
            print(f"PASS: all {len(listeners)} backend TCP listeners are on loopback", flush=True)
            if args.workers:
                endpoints = start_workers(1, os.environ.copy(), ls.LOG_DIR, worker_job)
                print("PASS: a search worker answers" if endpoints else "SKIP: search workers", flush=True)
            return
        profile = ls.session_profile(paths.RUNTIME, args.username)
        users_file = paths.RUNTIME / "users.json"
        users_file.write_text(json.dumps([{"id": profile["id"], "username": profile["username"]}]),
                              encoding="utf-8")
        env["GWENT_USERS_FILE"] = str(users_file)
        start("commservice", [sys.executable, str(paths.SERVER / "commservice.py"), "--standalone"], env)
        ls.wait_port(ls.GALAXY_PORT)
        game_env = dict(env)
        game_env.update(GWENT_SESSION_NET_DLL=str(paths.SESSION_NET),
                        GWENT_HOSTS_FILE=str(session_hosts(paths.RUNTIME / "session-hosts")),
                        GWENT_SESSION_CERT_SHA256=certificate_sha256(paths.CERTS / "fake.crt"),
                        GWENT_SESSION_NET_LOG=str(ls.LOG_DIR / "session_net.log"))
        games = paths.ROOT / "data" / "human-games"
        games.mkdir(parents=True, exist_ok=True)
        game_env["GWENT_GAMES_DIR"] = str(games)
        # The profile's name: a deck tagged with it (DeckSpec.Owner) is never
        # in the AI's prior when that profile plays.
        game_env["GWENT_PLAYER"] = profile["username"].lower()
        if args.stock_ai:
            game_env["GWENT_STOCK_AI"] = "1"
        elif args.workers > 0:
            endpoints = start_workers(args.workers, os.environ.copy(), ls.LOG_DIR, worker_job)
            if endpoints:
                game_env["GWENT_SEARCH_WORKERS"] = endpoints
        display = (["-screen-fullscreen", "0", "-screen-width", "1280", "-screen-height", "720"] if args.windowed
                   else ["-screen-fullscreen", "1", "-window-mode", "borderless"])
        log = (ls.LOG_DIR / "game.log").open("w", encoding="utf-8")
        ls.LOGS.append(log)
        # GWENT_GAME_EXE: another program in the game's place (tests).
        game_exe = os.environ.get("GWENT_GAME_EXE") or str(client / "Gwent.exe")
        game = subprocess.Popen([game_exe, *display], cwd=client, env=game_env,
                                stdout=log, stderr=subprocess.STDOUT)
        print(f"Launching Gwent as {profile['username']}. Close the game to end the session; "
              f"logs: {ls.LOG_DIR}, {client / 'MelonLoader' / 'Latest.log'}", flush=True)
        while game.poll() is None:
            for process in ls.PROCESSES:
                if process.poll() is not None:
                    raise RuntimeError(f"A backend component stopped ({process.args[-1]}); see {ls.LOG_DIR}")
            for worker in ls.WORKERS:
                pid = worker.process.pid
                worker.check()
                if worker_job and worker.process.pid != pid:
                    worker_job.add(worker.process.pid)
            time.sleep(1)
        if game.returncode == SESSION_NET_FAILED:
            print(f"Gwent was stopped before it started: the GwentSessionNet plugin could not route it to the "
                  f"local server. See {ls.LOG_DIR / 'session_net.log'} and "
                  f"{client / 'MelonLoader' / 'Latest.log'}", flush=True)
        elif game.returncode:
            print(f"Gwent exited with {game.returncode}", flush=True)
    finally:
        stop_all()
        if test_data:
            shutil.rmtree(test_data, ignore_errors=True)


def child_pids(parents):
    """Direct children of the given processes (via the Toolhelp snapshot)."""
    import ctypes
    from ctypes import wintypes

    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD), ("th32ProcessID", wintypes.DWORD),
                    ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wintypes.DWORD),
                    ("cntThreads", wintypes.DWORD), ("th32ParentProcessID", wintypes.DWORD),
                    ("pcPriClassBase", ctypes.c_long), ("dwFlags", wintypes.DWORD),
                    ("szExeFile", ctypes.c_wchar * 260)]
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snapshot = kernel32.CreateToolhelp32Snapshot(0x2, 0)
    entry = PROCESSENTRY32W()
    entry.dwSize = ctypes.sizeof(entry)
    found = set()
    ok = kernel32.Process32FirstW(snapshot, ctypes.byref(entry))
    while ok:
        if entry.th32ParentProcessID in parents:
            found.add(entry.th32ProcessID)
        ok = kernel32.Process32NextW(snapshot, ctypes.byref(entry))
    kernel32.CloseHandle(wintypes.HANDLE(snapshot))
    return found
