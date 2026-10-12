"""Where the Windows setup and session keep things, and the setup's state
(.runtime/windows.json: the client folder, a trusted certificate)."""

import json
from pathlib import Path
import runpy

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / ".runtime"
LOCAL = ROOT / ".local"
DOWNLOADS = LOCAL / "downloads"
SERVER = ROOT / "server"
SERVER_DATA = RUNTIME / "server-data"
CERTS = RUNTIME / "certs"
HTML = RUNTIME / "nginx-html"
STATE = RUNTIME / "windows.json"
VENV_PYTHON = ROOT / ".venv" / "Scripts" / "python.exe"
LOADER_DIR = LOCAL / "melonloader"
DIST = ROOT / "dist"
SESSION_NET = DIST / "session_net.dll"
PLUGIN = DIST / "GwentSessionNet.dll"

# Pinned downloads. MelonLoader: the release asset scripts/setup.sh pins.
# nginx: nginx.org's Windows build; the hash is of the zip whose PGP signature
# (nginx-1.30.5.zip.asc) verified against nginx.org's signing keys on 2026-10-12.
MELONLOADER_URL = "https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip"
MELONLOADER_SHA256 = "147d79e2d61ffd4d1da2811162478d3e22764bc2c5c93fa2c7f0e47dc792b4d0"
NGINX_VERSION = "1.30.5"
NGINX_URL = f"https://nginx.org/download/nginx-{NGINX_VERSION}.zip"
NGINX_SHA256 = "e5afe28b6a50bec92c478bfe1a4d3758206b80fb77159277bc5c4e88955c2a35"
NGINX_DIR = LOCAL / f"nginx-{NGINX_VERSION}-windows"
NGINX = NGINX_DIR / "nginx.exe"
GWENT_EXE_SHA256 = "bacc0d9774640213f698ae0400023041f8df5aba753ea595fa3b449206198231"
# Read the client's deck definitions (scripts/client_decks.py), as setup.sh.
UNITYPY = ["UnityPy==1.25.3", "TypeTreeGeneratorAPI==0.0.10"]


def gog_hosts():
    """The GOG host list (server/gog_hosts.py, data only)."""
    return runpy.run_path(str(SERVER / "gog_hosts.py"))["GOG_HOSTS"]


def load_state():
    if not STATE.is_file():
        raise SystemExit("Run scripts\\setup.cmd first.")
    return json.loads(STATE.read_text(encoding="utf-8"))


def save_state(state):
    STATE.parent.mkdir(parents=True, exist_ok=True)
    STATE.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8", newline="\n")
