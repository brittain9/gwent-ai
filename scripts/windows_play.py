#!/usr/bin/env python3
"""Play Practice against the AI on Windows, natively.

    scripts\\setup.cmd [--client DIR] [--trust-cert | --untrust-cert]   once, and after an update
    scripts\\run-local.cmd [--windowed] [--username NAME] [--workers N] [--stock-ai] [--smoke]
    scripts\\setup.cmd --uninstall        take everything back out of the client

Setup writes only inside this folder and the client folder: pinned downloads
(MelonLoader, nginx, the workers' NuGet packages) in .local, the server's
data, a certificate of this install and the client's static responses in
.runtime, and MelonLoader, the mods, card data, decks and two settings files
in the client. A session starts the local server, nginx and the Galaxy
stand-in on 127.0.0.1 and the search workers, then the game; the game finds
the server through the GwentSessionNet plugin inside its own process, which
also accepts this install's certificate (by its SHA-256) for the routed GOG
names and keeps the game's own listeners on loopback. Nothing needs
administrator rights: no hosts file, no Windows service, no DNS, firewall or
certificate-store change.

--trust-cert is a fallback, off by default, for the case where the in-process
acceptance does not cover some client component: it adds this install's
certificate (an end-entity certificate, never an authority) to your user's
trusted roots, the only change to Windows setup can make; --untrust-cert and
--uninstall remove it.
"""

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

import windows_paths as paths

ROOT = paths.ROOT
OUR_MODS = ("GwentBetaRestorationMod.dll", "GwentAI.dll")
DIST_REQUIRED = ("GwentAI.dll", "GwentBetaRestorationMod.dll", "host-core/GwentAI.dll",
                 "host-core/GwentHost.Core.dll", "host-core/GwentHost.Core.deps.json",
                 "host-core/GwentHost.Core.runtimeconfig.json")
SETTINGS = ("config.json", "Launch.cfg")
SETTINGS_DIR = "Gwent_Data/StreamingAssets/Settings"
# What MelonLoader puts in the client (its zip, and UserData at first start),
# and the folders setup makes; uninstall removes only those setup added.
LOADER_PATHS = ("version.dll", "NOTICE.txt", "MelonLoader", "UserData", "Mods", "Plugins")


def say(text):
    print(text, flush=True)


def step(text):
    say(f"\n== {text}")


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def extract(archive_path, destination, strip=None):
    """Extracts a zip below destination, refusing paths outside it; strip
    removes a leading folder. Returns the relative paths written."""
    destination = Path(destination).resolve()
    written = []
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.infolist():
            name = entry.filename
            if strip:
                if not name.startswith(strip + "/"):
                    continue
                name = name[len(strip) + 1:]
            if not name:
                continue
            target = (destination / name).resolve()
            if not target.is_relative_to(destination):
                raise SystemExit(f"Unsafe path in {archive_path}: {entry.filename}")
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(archive.read(entry))
            written.append(name)
    return written


def python_env():
    return dict(os.environ, PYTHONUTF8="1", GWENT_CLIENT=str(CLIENT))


def venv_run(*arguments, **kwargs):
    subprocess.run([str(paths.VENV_PYTHON), *map(str, arguments)], check=True, env=python_env(), cwd=ROOT, **kwargs)


def certutil(*arguments, runner=subprocess.run):
    return runner(["certutil", "-user", *arguments], capture_output=True, text=True)


def current_thumbprint():
    import selfsigned
    return selfsigned.thumbprint(selfsigned.pem_der((paths.CERTS / "fake.crt").read_text(encoding="ascii")))


def trust_certificate(state, runner=subprocess.run):
    """Adds this install's certificate to the user's Root store (Windows asks
    you to confirm) and records its thumbprint for --untrust-cert. A
    certificate of this install trusted before is removed; when that fails it
    is kept in the state's "stale_trusted", which --untrust-cert and
    --uninstall try again, and the new one is trusted all the same."""
    import selfsigned
    cert = selfsigned.pem_der((paths.CERTS / "fake.crt").read_text(encoding="ascii"))
    if not selfsigned.is_ours(cert):
        raise SystemExit("refusing to trust a certificate this setup did not make")
    result = certutil("-addstore", "Root", str(paths.CERTS / "fake.crt"), runner=runner)
    if result.returncode:
        raise SystemExit(f"certutil -user -addstore failed: {result.stdout}{result.stderr}")
    previous = state.get("trusted")
    state["trusted"] = selfsigned.thumbprint(cert)
    say(f"  trusted for your user: {state['trusted']} (scripts\\setup.cmd --untrust-cert removes it)")
    if previous and previous != state["trusted"]:
        state.setdefault("stale_trusted", []).append(previous)
    remove_stale(state, runner)


def delete_trusted(thumbprint, runner):
    result = certutil("-delstore", "Root", thumbprint, runner=runner)
    if result.returncode:
        say(f"  certutil -user -delstore Root {thumbprint} failed; remove it in certmgr.msc "
            f"(Trusted Root Certification Authorities, \"Gwent AI local server\"): {result.stdout}{result.stderr}")
        return False
    say(f"  removed {thumbprint} from your user's trusted roots")
    return True


def remove_stale(state, runner=subprocess.run):
    """Removes earlier certificates of this install from the user's roots;
    keeps the ones it could not remove for the next try."""
    stale = [thumbprint for thumbprint in state.pop("stale_trusted", []) if not delete_trusted(thumbprint, runner)]
    if stale:
        state["stale_trusted"] = stale
    return not stale


def untrust_certificate(state, runner=subprocess.run):
    """Removes this install's trusted certificates; True when none is left."""
    thumbprint = state.pop("trusted", None)
    if thumbprint and not delete_trusted(thumbprint, runner):
        state["trusted"] = thumbprint
    return remove_stale(state, runner) and "trusted" not in state


DOTNET_MISSING = ("The .NET 10 runtime is needed: the AI's search workers and setup's deck check run on it. "
                  "Install it with\n    winget install Microsoft.DotNet.Runtime.10\n"
                  "then open a new window and run scripts\\setup.cmd again.")


def require_dotnet(which=shutil.which, runner=subprocess.run):
    """Stops setup unless dotnet runs and lists a .NET 10 runtime."""
    dotnet = which("dotnet")
    if not dotnet:
        raise SystemExit(DOTNET_MISSING)
    try:
        runtimes = runner([dotnet, "--list-runtimes"], capture_output=True, text=True).stdout or ""
    except OSError:
        raise SystemExit(DOTNET_MISSING)
    if not any(line.startswith("Microsoft.NETCore.App 10.") for line in runtimes.splitlines()):
        raise SystemExit(DOTNET_MISSING)


def choose_client(argument, state):
    client = Path(argument or state.get("client") or ROOT.parent / "client").resolve()
    if not (client / "Gwent.exe").is_file():
        raise SystemExit(f"No Gwent.exe in {client}. Extract Gwent 0.9.24.3.432 there or pass --client DIR.")
    return client


def setup(args):
    global CLIENT
    state = paths.load_state() if paths.STATE.is_file() else {}
    step("Client")
    CLIENT = choose_client(args.client, state)
    state["client"] = str(CLIENT)
    if sha256(CLIENT / "Gwent.exe") == paths.GWENT_EXE_SHA256:
        say(f"  {CLIENT}: Gwent.exe is 0.9.24.3.432")
    else:
        say(f"  warning: {CLIENT}\\Gwent.exe is not the 0.9.24.3.432 build this project uses")
    if args.uninstall:
        return uninstall(state)
    if args.untrust_cert:
        untrust_certificate(state)
        paths.save_state(state)
        return

    step("Prerequisites")
    if sys.version_info < (3, 10):
        raise SystemExit("Python 3.10 or later is needed (winget install Python.Python.3.12)")
    require_dotnet()
    say("  .NET 10 runtime: ready")

    step("The AI (dist)")
    import windows_build
    missing = [name for name in DIST_REQUIRED if not (paths.DIST / name).is_file()]
    if missing:
        raise SystemExit(f"Missing dist\\{', dist\\'.join(missing)}: extract the complete release next to the "
                         "client (or build: bash scripts/build-local.sh)")
    if sha256(paths.DIST / "GwentAI.dll") != sha256(paths.DIST / "host-core" / "GwentAI.dll"):
        raise SystemExit("dist\\GwentAI.dll and dist\\host-core\\GwentAI.dll differ; extract the complete release again")
    say("  dist: the AI and its search workers, one build")

    step("Downloads (.local)")
    import pinned
    import host_core_deps
    loader_zip = pinned.fetch(paths.MELONLOADER_URL, paths.DOWNLOADS / "MelonLoader.x64.zip",
                              sha256=paths.MELONLOADER_SHA256, say=say)
    if not (paths.LOADER_DIR / "MelonLoader" / "0Harmony.dll").is_file():
        extract(loader_zip, paths.LOADER_DIR)
        say("  extracted MelonLoader to .local\\melonloader")
    nginx_zip = pinned.fetch(paths.NGINX_URL, paths.DOWNLOADS / f"nginx-{paths.NGINX_VERSION}.zip",
                             sha256=paths.NGINX_SHA256, say=say)
    if not paths.NGINX.is_file():
        extract(nginx_zip, paths.NGINX_DIR, strip=f"nginx-{paths.NGINX_VERSION}")
        say(f"  extracted nginx {paths.NGINX_VERSION} to {paths.NGINX_DIR.relative_to(ROOT)}")
    host_core_deps.install(paths.DIST / "host-core", say=say)
    # Built when missing; in a source checkout also rebuilt after an update
    # (windows_build.needs_build). A downloaded release keeps its own.
    if windows_build.needs_build(paths.SESSION_NET, [windows_build.SESSION_NET_SOURCE], windows_build.mingw):
        windows_build.build_session_net()
        say("  built dist\\session_net.dll")
    if windows_build.needs_build(paths.PLUGIN, [windows_build.PLUGIN_PROJECT,
                                                *windows_build.PLUGIN_PROJECT.parent.glob("*.cs")],
                                 windows_build.dotnet_sdk):
        windows_build.build_plugin(CLIENT, paths.LOADER_DIR / "MelonLoader")
        say("  built dist\\GwentSessionNet.dll")

    step("Python environment for the server (.venv)")
    if not paths.VENV_PYTHON.is_file():
        subprocess.run([sys.executable, "-m", "venv", str(ROOT / ".venv")], check=True)
    venv_run("-m", "pip", "install", "--quiet", "--disable-pip-version-check",
             "-r", ROOT / "server" / "requirements.txt", *paths.UNITYPY)
    say("  .venv: ready")

    step("Your client")
    install_client_files(state, CLIENT, loader_zip)
    say("  MelonLoader 0.5.7, the restoration mod, the AI, the GwentSessionNet plugin and the settings")
    venv_run(ROOT / "scripts" / "install-card-data.py", "--client", CLIENT)
    if len(list((ROOT / ".runtime" / "decks" / "practice").glob("*.json"))) == 7:
        say("  have the 7 Practice decks")
    else:
        venv_run(ROOT / "scripts" / "client_decks.py")
        venv_run(ROOT / "scripts" / "roster.py", "import-client")
    venv_run(ROOT / "scripts" / "roster.py", "install", "--to", CLIENT / "Mods")

    step("Local server (.runtime)")
    import selfsigned
    if selfsigned.ensure(paths.CERTS):
        say("  made this install's certificate (.runtime\\certs)")
        if state.get("trusted"):
            args.trust_cert = True  # the new one replaces the trusted old one
    else:
        say("  have this install's certificate")
    if args.trust_cert and state.get("trusted") != current_thumbprint():
        trust_certificate(state)
    elif state.get("stale_trusted"):
        remove_stale(state)
    paths.save_state(state)
    venv_run(ROOT / "server" / "deploy" / "setup_local_server.py", CLIENT, "--data-dir", paths.SERVER_DATA, "--managed")
    import server_static
    server_static.write(CLIENT, paths.HTML)
    for directory in ("logs", "home"):
        (paths.RUNTIME / directory).mkdir(parents=True, exist_ok=True)
    paths.save_state(state)
    step("Done")
    say("Play: scripts\\run-local.cmd   (scripts\\run-local.cmd --smoke checks the server without the game)")


def backup_dir(client):
    """Where setup keeps the client's own copies of the files it replaces."""
    key = hashlib.sha256(str(Path(client).resolve()).lower().encode("utf-8")).hexdigest()[:16]
    return paths.RUNTIME / "client-backup" / key


def client_record(state, client):
    """What this client held before setup first changed it: the MelonLoader
    paths already there, and per settings file whether its original was
    saved ("saved"), absent ("absent") or already replaced by an earlier
    setup that kept no copy ("unknown"). Kept in the setup state until
    uninstall."""
    installs = state.setdefault("installs", {})
    key = str(Path(client).resolve())
    if key not in installs:
        installs[key] = {"preexisting": sorted(name for name in LOADER_PATHS if (Path(client) / name).exists()),
                         "settings": {}}
    return installs[key]


def install_client_files(state, client, loader_zip, dist=None, plugin=None, settings_source=None):
    """Writes MelonLoader, the mods, the plugin and the settings into the
    client; first records what was there and saves the original settings
    files (the state is saved before anything is overwritten)."""
    client = Path(client)
    dist, plugin = Path(dist or paths.DIST), Path(plugin or paths.PLUGIN)
    settings_source = Path(settings_source or ROOT / "server" / "settings")
    record = client_record(state, client)
    settings = client / SETTINGS_DIR
    backup = backup_dir(client)
    for name in SETTINGS:
        if name in record["settings"]:
            continue
        current, ours = settings / name, settings_source / name
        if not current.is_file():
            record["settings"][name] = "absent"
        elif sha256(current) == sha256(ours):
            record["settings"][name] = "unknown"
        else:
            backup.mkdir(parents=True, exist_ok=True)
            shutil.copy2(current, backup / name)
            record["settings"][name] = "saved"
    paths.save_state(state)
    if "version.dll" in record["preexisting"] or "MelonLoader" in record["preexisting"]:
        say("  note: this client already had MelonLoader; setup puts MelonLoader 0.5.7 over it, "
            "and --uninstall leaves MelonLoader in place")
    extract(loader_zip, client)
    (client / "Mods").mkdir(exist_ok=True)
    (client / "Plugins").mkdir(exist_ok=True)
    for mod in OUR_MODS:
        shutil.copy2(dist / mod, client / "Mods" / mod)
    shutil.copy2(plugin, client / "Plugins" / plugin.name)
    settings.mkdir(parents=True, exist_ok=True)
    for name in SETTINGS:
        shutil.copy2(settings_source / name, settings / name)


def uninstall(state, settings_source=None):
    """Removes what setup added to the client and puts back the files it
    replaced; removes the trusted certificate. What the client held before
    setup stays."""
    step("Uninstall")
    untrust_certificate(state)
    settings_source = Path(settings_source or ROOT / "server" / "settings")
    key = str(Path(CLIENT).resolve())
    record = state.get("installs", {}).get(key)
    removed, restored, kept = [], [], []

    def remove(path):
        path = Path(path)
        if path.is_dir():
            shutil.rmtree(path)
        elif path.exists():
            path.unlink()
        else:
            return
        removed.append(path.relative_to(CLIENT).as_posix())

    for mod in OUR_MODS:
        remove(CLIENT / "Mods" / mod)
    remove(CLIENT / "Mods" / "GwentAI-roster")
    remove(CLIENT / "Mods" / "GwentAI-layers.json")
    remove(CLIENT / "Plugins" / paths.PLUGIN.name)
    # The settings: the saved original back; a file setup added removed;
    # anything else (an original setup never saw, or one the player changed
    # since) stays.
    settings = CLIENT / SETTINGS_DIR
    backup = backup_dir(CLIENT)
    for name in SETTINGS:
        current, ours = settings / name, settings_source / name
        status = (record or {}).get("settings", {}).get(name)
        is_ours = current.is_file() and ours.is_file() and sha256(current) == sha256(ours)
        if status == "saved" and (backup / name).is_file():
            if is_ours or not current.exists():
                shutil.copy2(backup / name, current)
                (backup / name).unlink()
                restored.append(f"{SETTINGS_DIR}/{name}")
            else:
                kept.append(f"{SETTINGS_DIR}/{name} (changed since setup; the original is in {backup / name})")
        elif status == "absent" and is_ours:
            remove(current)
        elif is_ours:
            kept.append(f"{SETTINGS_DIR}/{name} (no copy of the client's original was kept; "
                        "take it from a fresh copy of the client)")
    # MelonLoader and the folders it makes at first start: only what setup added.
    preexisting = set(record["preexisting"]) if record else set(LOADER_PATHS)
    for name in ("version.dll", "NOTICE.txt", "MelonLoader", "UserData"):
        if name not in preexisting:
            remove(CLIENT / name)
        elif (CLIENT / name).exists():
            kept.append(name)
    remove(CLIENT / "UserData" / "GwentAI")  # the AI's card data, when UserData stays
    for folder in ("Mods", "Plugins"):
        if folder not in preexisting and (CLIENT / folder).is_dir() and not any((CLIENT / folder).iterdir()):
            remove(CLIENT / folder)
    if record is not None:
        state["installs"].pop(key)
        if backup.is_dir() and not any(backup.iterdir()):
            backup.rmdir()
    paths.save_state(state)
    say("  removed: " + (", ".join(removed) if removed else "nothing (already clean)"))
    if restored:
        say("  restored: " + ", ".join(restored))
    if kept:
        say("  left in place: " + "; ".join(kept))
    say(f"  This folder keeps its downloads and server data; delete {ROOT} to remove them too.")


def run(args):
    import windows_session
    import winjob
    if not paths.VENV_PYTHON.is_file() or Path(sys.executable).resolve() != paths.VENV_PYTHON.resolve():
        raise SystemExit("Run scripts\\run-local.cmd (it uses .venv, which scripts\\setup.cmd makes).")
    # Every process the session starts ends with it, even when this window
    # is closed; workers also get a memory ceiling.
    job = winjob.Job()
    job.add_current()
    memory_mb = int(os.environ.get("GWENT_WORKER_MEMORY_MB", "2048"))
    worker_job = winjob.Job(process_memory_mb=memory_mb * 3 // 2)
    if args.workers is None:
        import local_session
        args.workers = int(os.environ.get("GWENT_PRACTICE_WORKERS", local_session.default_workers()))
    windows_session.run(args, job, worker_job)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    setup_parser = sub.add_parser("setup", help="prepare this folder and the client (run once, and after updates)")
    setup_parser.add_argument("--client", help="the Gwent 0.9.24.3.432 folder (default: ..\\client, then the last one)")
    trust = setup_parser.add_mutually_exclusive_group()
    trust.add_argument("--trust-cert", action="store_true",
                       help="fallback, not needed normally: add this install's certificate to your "
                            "user's trusted roots")
    trust.add_argument("--untrust-cert", action="store_true", help="remove it again")
    trust.add_argument("--uninstall", action="store_true",
                       help="remove what setup added to the client, put back the settings files it replaced, "
                            "and remove any trusted certificate")
    setup_parser.set_defaults(func=setup)
    run_parser = sub.add_parser("run", help="play a session")
    run_parser.add_argument("--smoke", action="store_true",
                            help="check the local server and a search worker, then exit without the game")
    run_parser.add_argument("--username",
                            help="the local profile to play as, made when new (default: the last one; "
                                 "the first session names it after your login name)")
    run_parser.add_argument("--windowed", action="store_true")
    run_parser.add_argument("--stock-ai", action="store_true", help="Practice against the built-in AI")
    run_parser.add_argument("--workers", type=int,
                            help="search worker processes (0: search in the game process only; "
                                 "default: cores - 2, at most 6; GWENT_PRACTICE_WORKERS)")
    run_parser.set_defaults(func=run)
    args = parser.parse_args(argv)
    if sys.platform != "win32":
        raise SystemExit("Windows only; on Linux and macOS use scripts/setup.sh --play and scripts/run-local.sh")
    args.func(args)


CLIENT = None

if __name__ == "__main__":
    main()
