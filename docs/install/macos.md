# macOS

Tested on Apple silicon (macOS 26) for release 0.1.0. Intel Macs are
untested. Download
[gwent-ai-v0.1.0.zip](https://github.com/brittain9/gwent-ai/releases/download/v0.1.0/gwent-ai-v0.1.0.zip)
and extract it beside your [client](client.md), as `gwent-ai/` and `client/`.

## Prerequisites

Only Rosetta 2, which runs Wine and the game:

```sh
softwareupdate --install-rosetta --agree-to-license
```

Setup downloads everything else into `.local/` and checks each file's
SHA-256. It installs no system packages and never asks for root:

- Mono 6.14.1 for the search workers, prebuilt as x86_64. The workers must
  do floating-point arithmetic exactly as the client does, and native arm64
  Mono cannot. Built by `platform/macos/build-mono.sh`.
- nginx 1.30.5 with OpenSSL and PCRE2 linked in. Built by
  `platform/macos/build-nginx.sh`.
- uv, and the Python 3.12 it installs.
- Wine 11.0 from [Gcenx's macOS builds](https://github.com/Gcenx/macOS_Wine_builds),
  with MoltenVK, and [DXVK for macOS](https://github.com/Gcenx/DXVK-macOS)
  for Direct3D 11.
- MelonLoader 0.5.7.

## Install and play

From the extracted `gwent-ai` folder:

```sh
scripts/setup.sh --play --prebuilt
scripts/run-local.sh
```

Setup takes about a minute plus the downloads (about 350 MB). It derives
the card data from your client and installs the AI, roster and card data
into it. Choose **Single-player, then Practice**. Close the game to stop
the session. Setup prints a card-table hash; compare it with the
[release notes](../../results/v0.1.0.md).

The game uses the display's full pixel size. Options such as `--windowed`,
`--stock-ai` and `--workers N` work as on [Linux](linux.md).
To update, extract the next complete release and run setup again.

## Known issue: card art

Some cards show their frame, numbers and effects, but not their art. The
game's Direct3D 11 runs on Metal through DXVK and MoltenVK, and Metal has
no transform feedback, which these cards need. The game log shows
`Transform feedback not supported by device`. The cards still play
correctly. Wine's own Direct3D and the game's OpenGL mode do not start the
game, so DXVK is the only working renderer on macOS.

## Build from source

Install the Xcode Command Line Tools (`xcode-select --install`), Python
3.10+ and uv, and `brew install --cask dotnet-sdk` and `brew install mingw-w64`.
Run `scripts/setup.sh --play` without `--prebuilt`, then `scripts/run-local.sh`. After changing source, run
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
