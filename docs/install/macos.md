# macOS

Tested on Apple silicon. Intel Macs are untested. First get the client and
put it in `../client` ([client.md](client.md)).

## Prerequisites

| Needs | Install | Tested with |
| --- | --- | --- |
| Xcode Command Line Tools | `xcode-select --install` | |
| Rosetta 2 | `softwareupdate --install-rosetta` | |
| .NET SDK | `brew install --cask dotnet-sdk` | 10.0.401 |
| Python 3.10+ | `brew install python` | 3.14 |
| uv | `brew install uv` | 0.10 |
| nginx | `brew install nginx` | 1.31 |
| MinGW-w64 GCC | `brew install mingw-w64` | 14.0 |
| openssl | `brew install openssl` | |

`scripts/setup.sh --play` checks all of these and prints the Homebrew
command for anything missing. It never installs system packages and never
asks for root. It downloads the rest, pinned and checked by SHA-256:

- **Mono 6.14.1**, built from source into `.local/mono-x86_64` (about 20
  minutes, once). The search workers must round floating-point arithmetic
  the way the client's own Mono does; Homebrew's arm64 Mono does not, so
  the workers refuse to run on it. Rosetta 2 runs the built one.
- **Wine 11.0**, Gcenx's macOS build
  ([macOS_Wine_builds](https://github.com/Gcenx/macOS_Wine_builds)), an
  x86_64 program that Rosetta 2 runs. It bundles MoltenVK (Vulkan on Metal).
- **DXVK 1.10.3 for macOS** ([DXVK-macOS](https://github.com/Gcenx/DXVK-macOS)),
  Direct3D 11 on MoltenVK.

The game renders at the display's full pixel size (Retina mode).

## Steps

```sh
scripts/setup.sh --play      # checks prerequisites, downloads the pinned files, builds the AI against your client
server/fetch-upstream.sh     # once: fetches upstream's service files for the community server
scripts/prepare-local.py     # installs MelonLoader, the AI and its deck roster into the client
scripts/run-local.sh         # starts the server, the game under Wine and the search workers
```

In the game choose **Single-player**, then **Practice**. Close the game to
stop the session.

- `setup.sh` is safe to run again: finished steps are skipped. After an
  update of the project, run `scripts/build-local.sh` and
  `scripts/prepare-local.py` again.
- The project ships our copy of Somnia41205's community server under
  `server/`. `server/fetch-upstream.sh` downloads the service files that
  only upstream carries; run it once.
- The build reads the card tables from your own client; nothing from the
  game is in the project. It prints a `card table: sha256 ...` line. The
  value should match the one in the release notes (the `results/` file for
  your release). A different value means your client differs from the one
  the release was measured with.
- `run-local.sh` takes the same options as on Linux ([linux.md](linux.md)):
  `--windowed`, `--stock-ai`, `--username`, `--workers N`.

## Isolation

macOS has no unprivileged network namespaces, so `run-local.sh` uses the
system's kernel sandbox (`sandbox-exec`) on the server, nginx, Wine and
everything they start: loopback networking only, writes only to `.runtime`
and the client, no root and no system setting changes. A small library
loaded into every Wine process resolves the GOG service names to
127.0.0.1, where nginx listens on port 10443.

Differences from Linux: the session shares the Mac's loopback, so other
programs on the Mac can reach the server's ports, and the session refuses
to start if one of its ports (10443, 7777, 8443–8447, 9977) is already in
use. The sandbox limits writes, not reads: the client could read your
files. As on Linux, this is practical isolation, not a guarantee against a
malicious client, and the server must not be exposed to a network.

## Logs

As on Linux ([linux.md](linux.md#logs)). The Wine prefix's Unity log is
`.runtime/wine/drive_c/users/<you>/AppData/LocalLow/CDProjektRED/Gwent/output_log.txt`.
