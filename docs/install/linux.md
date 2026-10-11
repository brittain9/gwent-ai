# Linux

Tested on Fedora 44, x86-64. Package names for
Debian and Ubuntu are given but untested. First get the client and put it in
`../client` ([client.md](client.md)).

## Prerequisites

| Needs | Fedora | Debian / Ubuntu | Tested with |
| --- | --- | --- | --- |
| .NET SDK | `dotnet-sdk-10.0` | `dotnet-sdk-10.0` (or Microsoft's feed) | 10.0.112 |
| Mono 6 | `mono-core` | `mono-complete` | 6.14.1 |
| Python 3.10+ | `python3` | `python3` | 3.14 |
| uv | `uv` | `pipx install uv` | 0.10 |
| curl, unzip, tar | `curl unzip tar` | `curl unzip tar` | |
| Wine (64-bit, WoW64) | `wine` | `wine wine64` | 11.0 |
| nginx | `nginx` | `nginx` | 1.30 |
| MinGW-w64 GCC | `mingw64-gcc` | `gcc-mingw-w64-x86-64` | 16.2 |
| bubblewrap, unshare, ip, openssl | `bubblewrap util-linux iproute openssl` | `bubblewrap util-linux iproute2 openssl` | |
| Vulkan GPU driver (for DXVK) | your GPU's driver | your GPU's driver | |
| Unprivileged user namespaces | on by default | blocked by AppArmor on Ubuntu 24.04+ (see below) | |

`scripts/setup.sh --play` checks all of these and prints the install
command for anything missing. It never installs system packages and never
asks for root.

## Steps

```sh
scripts/setup.sh --play      # checks prerequisites, downloads the pinned files, builds the AI against your client
server/fetch-upstream.sh     # once: fetches upstream's service files for the community server
scripts/prepare-local.py     # installs MelonLoader, the AI and its deck roster into the client
scripts/run-local.sh         # starts the server, the game under Wine and the search workers
```

In the game choose **Single-player**, then **Practice**. Close the game to
stop the session.

- `setup.sh` downloads pinned files into `.local/` and checks their SHA-256.
  It is safe to run again: finished steps are skipped. After an update of
  the project, run `scripts/build-local.sh` and `scripts/prepare-local.py`
  again.
- The project ships our copy of Somnia41205's community server under
  `server/`. `server/fetch-upstream.sh` downloads the service files that
  only upstream carries; run it once.
- The build reads the card tables from your own client; nothing from the
  game is in the project. It prints a `card table: sha256 ...` line. The
  value should match the one in the release notes (the `results/` file for
  your release). A different value means your client differs from the one
  the release was measured with.
- `run-local.sh --windowed` uses a 1280×720 window instead of borderless
  full screen. `--stock-ai` plays Practice against the client's own AI.
  Your profile is named after your login name (`--username` changes it on
  first start).
- The AI searches on worker processes, `min(6, cores − 2)` of them by
  default (`--workers N` or `GWENT_PRACTICE_WORKERS=N`; `0` turns them off,
  which makes the AI weaker). More cores make it stronger. Other settings,
  such as the think time: [options.md](options.md).

**Ubuntu 24.04 and later** restrict unprivileged user namespaces through
AppArmor, which `unshare` and `bwrap` need. `setup.sh` reports it. Options
(untested here): allow them for the session
(`sudo sysctl kernel.apparmor_restrict_unprivileged_userns=0`), or add an
AppArmor profile that allows `userns` for `/usr/bin/bwrap` and
`/usr/bin/unshare`.

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
