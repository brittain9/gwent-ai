# Where this server comes from: the Unofficial Gwent Beta private server

This directory began as a copy of
[Unofficial Gwent Beta 0.9.24.3.432 Private Server](https://github.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server)
by **Somnia41205**. Their work makes the 2018 client usable again: the Python
backend that replaces CD PROJEKT RED's online services, the nginx routing, the
Galaxy service stand-in and the client restoration mod. This project's
Practice games run on it. Thank you.

It is now our own source, maintained here: players download and run nothing
from upstream, and setup fetches nothing from upstream's repository.
Upstream's license is [LICENSE](LICENSE) (MIT, © 2026 Somnia41205), kept
verbatim; third-party parts keep theirs ([docs/NOTICE.md](docs/NOTICE.md)).

## Which upstream version

| | |
| --- | --- |
| Upstream commit | `8295512683514fd4e6c79168d9f129716aaaf8d4` (main, 2026-07-27, "Update README.md") |
| Code identical to | tag `v1.0.2` (`b8b4d83`); `8295512` changes only README.md |
| Layout | `server/` is upstream's repository root, so paths match upstream's |

How the commit was found: the imported file tree was compared with every
upstream commit. Against `8295512` and `v1.0.2`, all 90 shared files differ
only in the changes listed below; [README.md](README.md) matches `8295512`
exactly (it has the Discord line that `8295512` added). Comparisons ignore
line endings (`.gitattributes` normalizes them).

## The files upstream's server serves from its own repository

Upstream keeps 15 response files derived from the game's online services
(`Nginx/html/`, `deploy/static/`, `deploy/rewards.json`). We neither keep nor
fetch them:

| Upstream file | Here |
| --- | --- |
| `Nginx/html/game/0.9.24_<secret>/config.json`, `news.json`, `news/1/GOG/news.initial.en-us`, `shop/platforms/GOG/products` | Made at setup and at each session from the player's client by [scripts/server_static.py](../scripts/server_static.py): the game version and data secret in the path come from the client's Settings asset, the bodies are upstream's (MIT), byte for byte (its test checks the hashes) |
| `deploy/rewards.json` | Upstream's is an empty list; `deploy/setup_local_server.py` writes the same, and `deploy/server.py` serves it when the file is missing |
| `Nginx/html/50x.html`, `index.html` | nginx's default pages; the client never asks for them |
| `deploy/static/` | A copy of `Nginx/html/` for upstream's own deployment; unused |
| `Nginx/html/card_definitions/prices` | The crafting, milling and transmutation costs per rarity. The client does not hold them, so they cannot be made from it. The project does not ship it. Without it `scripts/server_static.py` serves `{"base_prices": [], "price_overrides": []}`: the client asks for the prices during login (state 1007) and returns to the start of login when the request fails, and it reads both lists without a null check, so neither may be missing. What is lost: the collection's crafting values (`OnlineCollectionController.InitializeCollectionCard` reads each missing cost as 0), so the collection shows every card as costing 0 scraps to craft, giving 0 to mill and 0 powder to transmute; what crafting or milling then does depends on the server's own handling. Practice games do not use the prices |

## What we removed

Prebuilt binaries, upstream's certificate and the build scripts for upstream's
launchers. We build the binaries from source or use other builds.

| Upstream file | Why | Replacement |
| --- | --- | --- |
| `GwentBetaRestorationMod/GwentBetaRestorationMod.dll` | Prebuilt mod | Built from `GwentBetaModMain.cs` (byte-identical to upstream) with our `.csproj` |
| `comet-main/dummy-service/GalaxyCommunication.exe` | Prebuilt Windows service | Built from `communication_wine.c` with MinGW (`scripts/build-local.sh`); on Windows no service at all (`platform/windows/session_net.c` answers the SDK's service query inside the game) |
| `Nginx/nginx.exe` | Windows nginx build | nginx from the distribution, built from source, or nginx.org's Windows build pinned by hash |
| `Nginx/conf/fake.crt`, `Nginx/conf/fake.key` | One certificate for every user, and a certificate authority (CA:TRUE) whose private key is public upstream: a machine that trusts it trusts anyone holding the key | Each install makes its own end-entity certificate in `.runtime/certs` (`scripts/prepare-local.py`, `scripts/selfsigned.py`); every consumer reads it from there (`GWENT_CERT_DIR`) |
| `GwentBetaLauncher.spec`, `GwentServerHost.spec`, `build_launcher.bat`, `build_launcher_linux.sh`, `build_server_host.bat` | They package upstream's launchers as executables with the shared certificate | None: this project runs the server from source |
| `launcher.py`, `linux_launcher.py`, `https_proxy.py`, `server.txt`, `server_host.txt`, `deploy/server_host_main.py`, `deploy/server_host_gui.py`, `deploy/setup_server.sh`, `deploy/nginx-gwent.conf`, `docs/PLAYER_GUIDE.md`, `docs/HOST_GUIDE.md`, `docs/SERVER_SETUP.md` | Upstream's client launchers, its Windows server host and its public-server deployment, which needs those launchers: they install a certificate in the system's trusted roots, redirect the system's DNS, turn IPv6 off, register a Windows service or open firewall ports | This project's setup and session scripts, which change nothing outside the project and client folders; the GOG host list moved to `gog_hosts.py` |

## What we changed

Compared with `8295512`. Everything else is byte-identical apart from line
endings.

| File | Change |
| --- | --- |
| `deploy/broker.py` | Binds to `GWENT_BIND_HOST` (default `127.0.0.1`) instead of `0.0.0.0` |
| `deploy/relay.py` | Same bind change. No `pip install websockets` at run time; imports `websockets.legacy.server` (websockets 15) |
| `deploy/server.py` | Same bind change for ports 8443 and 8447; the certificate defaults to the project's `.runtime/certs` |
| `docs/NOTICE.md` | Says the binaries above were removed |
| `.gitignore`, `.gitattributes` | Merged into the repository root's files, with `server/` paths |

## What we added

| File | Purpose |
| --- | --- |
| `GwentBetaRestorationMod/GwentBetaRestorationMod.csproj` | Builds the restoration mod from source |
| `requirements.txt` | The backend's one dependency, pinned (`websockets==15.0.1`) |
| `gog_hosts.py` | The GOG service names each session sends to 127.0.0.1 (from upstream's `linux_launcher.py`) |
| `UPSTREAM.md` | This file |

Our runners are outside this directory:
on Linux and macOS `scripts/prepare-local.py`, `scripts/run-local.sh`,
`scripts/local_session.py` and `scripts/build-local.sh`, which run the backend
in a sandbox of its own; on Windows `scripts/windows_play.py` and
`scripts/windows_session.py`, with `platform/windows`. They read the GOG host
list from `gog_hosts.py`.
