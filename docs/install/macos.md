# macOS

Documented for Apple silicon with Wine and Rosetta 2. This release has not
been retested on macOS; Intel Macs are untested. Download
[gwent-ai-v0.1.0.zip](https://github.com/brittain9/gwent-ai/releases/download/v0.1.0/gwent-ai-v0.1.0.zip)
and extract it beside your [client](client.md), as `gwent-ai/` and `client/`.

## Prerequisites

| Needs | Install |
| --- | --- |
| Xcode Command Line Tools | `xcode-select --install` |
| Rosetta 2 | `softwareupdate --install-rosetta` |
| Python 3.10+ | `brew install python` |
| uv | `brew install uv` |
| nginx | `brew install nginx` |
| openssl | `brew install openssl` |

The download includes the AI and matching Mono workers. The .NET SDK and
MinGW compiler are needed only when building the AI from source.

Setup downloads pinned files and verifies their SHA-256 hashes:

- Mono 6.14.1, built as x86_64 under `.local/mono-x86_64` (about 20 minutes,
  once). The workers require the same floating-point arithmetic as the
  client; native arm64 Mono cannot run them correctly.
- Wine 11.0 from [Gcenx's macOS builds](https://github.com/Gcenx/macOS_Wine_builds),
  with MoltenVK. Rosetta 2 runs it.
- [DXVK for macOS](https://github.com/Gcenx/DXVK-macOS), for Direct3D 11.

Setup also compiles the small macOS network helper with Xcode's compiler.
It never installs system packages or asks for root.

## Install and play

From the extracted `gwent-ai` folder:

```sh
scripts/setup.sh --play --prebuilt
server/fetch-upstream.sh
scripts/prepare-local.py
scripts/run-local.sh
```

Preparation installs the AI, roster and card data derived from your client.
Choose **Single-player, then Practice**. Close the game to stop the session.
Setup prints a card-table hash; compare it with the [release notes](../../results/v0.1.0.md).

The game uses the display's full pixel size. Options such as `--windowed`,
`--stock-ai` and `--workers N` work as on [Linux](linux.md).
To update, extract the next complete release and repeat setup and preparation.

## Build from source

Also install `brew install --cask dotnet-sdk` and `brew install mingw-w64`.
Run `scripts/setup.sh --play` without `--prebuilt`, then the fetch,
preparation and run commands above. After changing source, run
`scripts/build-local.sh` and `scripts/prepare-local.py` again.

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
