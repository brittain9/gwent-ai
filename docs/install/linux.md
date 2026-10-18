# Linux

Tested on Fedora 44, x86-64. Debian and Ubuntu package names are provided
but untested. [Download the latest release](https://github.com/brittain9/gwent-ai/releases/latest)
and extract it beside your [client](client.md): the folders should be
`gwent-ai/` and `client/`.

## Prerequisites

The download contains the AI, matching search workers and server helpers.
It needs Mono 6 to run the workers; no .NET SDK or C compiler is needed.

| Needs | Fedora | Debian / Ubuntu |
| --- | --- | --- |
| Mono 6 | `mono-core` | `mono-complete` |
| Python 3.10+ | `python3` | `python3` |
| uv | `uv` | `pipx install uv` |
| curl, unzip, tar | `curl unzip tar` | `curl unzip tar` |
| Wine (64-bit, WoW64) | `wine` | `wine wine64` |
| nginx | `nginx` | `nginx` |
| bubblewrap, unshare, ip, openssl | `bubblewrap util-linux iproute openssl` | `bubblewrap util-linux iproute2 openssl` |
| Vulkan GPU driver (for DXVK) | your GPU's driver | your GPU's driver |
| Unprivileged user namespaces | on by default | see Ubuntu note below |

Setup checks these prerequisites and prints install commands for missing
packages. It never installs system packages or asks for root.

## Install and play

From the extracted `gwent-ai` folder:

```sh
scripts/setup.sh --play --prebuilt
scripts/run-local.sh
```

Setup downloads the pinned MelonLoader and DXVK files, checks their hashes,
derives the card data from your client, and installs the AI, roster and
card data into the client. Choose **Single-player, then Practice**.
Close the game to stop the session.

- Setup prints `card table: sha256 db0dbb0424c3e29b`; the same value means
  your client matches the one the AI was measured with.
- The AI uses up to six helper processes on Mono. Set `--workers N` or
  `GWENT_PRACTICE_WORKERS=N` to change this. `0` makes the AI search in the
  game process, which is weaker. Other settings: [options.md](options.md).
- `scripts/run-local.sh --windowed` uses a 1280×720 window.
  `--stock-ai` plays against the game's own AI.
- To update, extract the next complete release beside your client and run
  its setup. Keep the AI and workers from the same release.

**Ubuntu 24.04 and later** restrict unprivileged user namespaces through
AppArmor. Setup reports this. Options (untested here): allow them for the
session (`sudo sysctl kernel.apparmor_restrict_unprivileged_userns=0`) or
add an AppArmor profile allowing `userns` for `/usr/bin/bwrap` and `/usr/bin/unshare`.

## Emotes and arena

Both are on by default.

- **Emotes.** The opponent uses the game's taunts like a person: some games
  none, some games a few. To turn them off, set `GWENT_AI_EMOTES=0` before
  you start the session.
- **Arena, an early version.** The game's own arena screens work against
  the AI: a free ticket, a draft of 26 cards, a leader, and a run that ends
  at 9 wins or 3 losses. The AI drafts its deck at random, so it is easier
  to beat in arena than in Practice. To turn it off, set `GWENT_AI_ARENA=0`
  before you start the session. You get one new ticket each time the game
  starts, so restart the client for a new run.

Set them in the shell that runs `scripts/run-local.sh`, for example
`GWENT_AI_EMOTES=0 scripts/run-local.sh`.

## Build from source

To build instead, also install .NET SDK 10 and MinGW-w64 GCC
(`dotnet-sdk-10.0 mingw64-gcc` on Fedora;
`dotnet-sdk-10.0 gcc-mingw-w64-x86-64` on Debian/Ubuntu, using Microsoft's
SDK feed if needed). Run `scripts/setup.sh --play` without `--prebuilt`,
then `scripts/run-local.sh`. After changing
source, run `scripts/build-local.sh` and `scripts/prepare-local.py` again.

## Isolation

`run-local.sh` runs the server and the game in unprivileged user, network,
mount and PID namespaces: the GOG hostnames resolve to the local server
inside a private network with no external route, and no system hosts file,
DNS, firewall or service setting changes. The rest of `/home`, `/opt` and
`/tmp` is hidden; only the client and `.runtime` are writable. The game
keeps the GPU, the X11 display and your audio socket. This is practical
isolation, not a guarantee against a malicious client.

The server uses fake authentication, and the restoration mod accepts any
TLS certificate, for compatibility with the old client. Do not expose the
server to a network. The local certificate is not added to the system trust
store.

## Logs

All runtime state is under `.runtime` (ignored by Git).

| Path | Contents |
| --- | --- |
| `.runtime/logs/` | Server, Galaxy helper, DNS, Wine setup, search worker and game logs |
| `.runtime/wine/drive_c/users/<you>/AppData/LocalLow/CDProjektRED/Gwent/output_log.txt` | Unity's log |
| `../client/MelonLoader/Latest.log` | Loader and mod status; shows whether `GwentAI` loaded |
