# Vendored upstream: the Unofficial Gwent Beta private server

This directory is a copy of
[Unofficial Gwent Beta 0.9.24.3.432 Private Server](https://github.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server)
by **Somnia41205**. Their work makes the 2018 client usable again: the Python
backend that replaces CD PROJEKT RED's online services, the nginx routing, the
Galaxy service stand-in and the client restoration mod. This project's
Practice games run on it. Thank you.

We maintain this copy ourselves. It is not a fork that tracks upstream
automatically. Upstream's license is [LICENSE](LICENSE) (MIT, © 2026
Somnia41205); third-party parts keep theirs ([docs/NOTICE.md](docs/NOTICE.md)).

## Which upstream version

| | |
| --- | --- |
| Upstream commit | `8295512683514fd4e6c79168d9f129716aaaf8d4` (main, 2026-07-27, "Update README.md") |
| Code identical to | tag `v1.0.2` (`b8b4d83`); `8295512` changes only README.md |
| Layout | `server/` is upstream's repository root, so paths match upstream's |

The 15 files derived from the game's online services (`Nginx/html/`,
`deploy/static/`, `deploy/rewards.json`) come from upstream at this commit:
[fetch-upstream.sh](fetch-upstream.sh) downloads them and checks them against
[upstream-files.sha256](upstream-files.sha256).

How the commit was found: the imported file tree was compared with every
upstream commit. Against `8295512` and `v1.0.2`, all 90 shared files differ
only in the changes listed below; [README.md](README.md) matches `8295512`
exactly (it has the Discord line that `8295512` added). Comparisons ignore
line endings (`.gitattributes` normalizes them).

## What we removed

Prebuilt binaries and a private key. We build the binaries from source or
use other builds.

| Upstream file | Why | Replacement |
| --- | --- | --- |
| `GwentBetaRestorationMod/GwentBetaRestorationMod.dll` | Prebuilt mod | Built from `GwentBetaModMain.cs` (byte-identical to upstream) with our `.csproj` |
| `comet-main/dummy-service/GalaxyCommunication.exe` | Prebuilt Windows service | Built from `communication_wine.c` with MinGW (`scripts/build-local.sh`) |
| `Nginx/nginx.exe` | Windows nginx build | Linux nginx (distro package or built from source) |
| `Nginx/conf/fake.key` | Private TLS key, the same for every user | `scripts/prepare-local.py` makes a new key and certificate in `.runtime/certs` |

## What we changed

Compared with `8295512`. Everything else is byte-identical apart from line
endings.

| File | Change |
| --- | --- |
| `deploy/broker.py` | Binds to `GWENT_BIND_HOST` (default `127.0.0.1`) instead of `0.0.0.0` |
| `deploy/relay.py` | Same bind change. No `pip install websockets` at run time; imports `websockets.legacy.server` (websockets 15) |
| `deploy/server.py` | Same bind change for ports 8443 and 8447 |
| `server.txt`, `server_host.txt` | Default server `127.0.0.1` instead of upstream's public host |
| `docs/NOTICE.md` | Says the binaries above were removed |
| `.gitignore`, `.gitattributes` | Merged into the repository root's files, with `server/` paths |

The three Python changes are 16 added and 17 removed lines.

## What we added

| File | Purpose |
| --- | --- |
| `GwentBetaRestorationMod/GwentBetaRestorationMod.csproj` | Builds the restoration mod from source |
| `requirements.txt` | The backend's one dependency, pinned (`websockets==15.0.1`) |
| `UPSTREAM.md` | This file |

Our Linux runner is outside this directory: `scripts/prepare-local.py`,
`scripts/run-local.sh`, `scripts/local_session.py` and `scripts/build-local.sh`.
It runs the backend in its own network namespace. It does not use upstream's
launchers (`launcher.py`, `linux_launcher.py`); `prepare-local.py` only reads
the GOG host list from `linux_launcher.py`.

## Taking upstream changes

Upstream is small and changes rarely. Compare by hand:

```sh
git clone https://github.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server /tmp/upstream
git -C /tmp/upstream diff 8295512 HEAD --stat     # what changed upstream since our import
git -C /tmp/upstream diff 8295512 HEAD > /tmp/upstream.patch
git apply --directory=server --reject --exclude='*.exe' --exclude='*.dll' --exclude='*.key' /tmp/upstream.patch
```

Then:

1. Leave out new binaries and keys (`.gitignore` blocks `*.exe` and `*.dll`).
2. Keep the changes listed above, especially the `127.0.0.1` binds.
3. Update the commit and the tables in this file.
4. Run `scripts/run-local.sh --smoke`.

`git subtree pull --prefix=server <url> main --squash` also works on this
layout. There is no subtree history yet, so the first pull will conflict
on every changed file; the manual diff above is simpler.
