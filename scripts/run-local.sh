#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
[[ -x "$ROOT/.venv/bin/python" ]] || { echo 'run-local: no .venv; run scripts/setup.sh --play' >&2; exit 1; }
# Practice think time: up to 30 s is fine (the game's own turn limit). Settled decisions stop at half the budget and open ones extend to 1.5x it
# (PracticeAI ThinkExtend), so a 20 s budget gives about 10 s for most plays and at
# most 30 s. Override per run (13333: about 6.7 s, at most 20 s).
export GWENT_AI_THINK_MS="${GWENT_AI_THINK_MS:-20000}"
# Practice's search workers on .NET: traces identical to Mono and about 12% less
# time per simulation, so more simulations fit the same think time. GWENT_WORKER_RUNTIME=mono to go back.
export GWENT_WORKER_RUNTIME="${GWENT_WORKER_RUNTIME:-coreclr}"

# Practice games are recorded in data/human-games; the sandbox needs the directory.
mkdir -p "$ROOT/data/human-games"
# The Python program that runs the session in the sandbox: scripts/local_session.py,
# or another that extends it (GWENT_SESSION_PROGRAM, relative to the project root).
export GWENT_SESSION_PROGRAM="${GWENT_SESSION_PROGRAM:-scripts/local_session.py}"

if [[ "$(uname -s)" == Darwin ]]; then
    # Wine: GWENT_WINE_ROOT, else the build setup.sh extracted to .local.
    WINE_ROOT="${GWENT_WINE_ROOT:-$ROOT/.local/wine-macos}"
    [[ -x "$WINE_ROOT/bin/wine" ]] || { echo "run-local: no Wine at $WINE_ROOT; run scripts/setup.sh --play" >&2; exit 1; }
    [[ -f "$ROOT/dist/session_net.dylib" ]] || { echo 'run-local: no dist/session_net.dylib; run scripts/build-local.sh' >&2; exit 1; }
    if [[ -n "${GWENT_EPHEMERAL:-}" ]]; then
        echo 'run-local: GWENT_EPHEMERAL needs overlay mounts, which macOS does not offer; run one session at a time' >&2
        exit 1
    fi
    CLIENT="$(cd -- "$ROOT/../client" 2>/dev/null && pwd -P)" \
      || { echo "run-local: no client at $ROOT/../client; see the install guide" >&2; exit 1; }
    # The kernel confines the session and everything it starts to loopback
    # networking and writes to .runtime, the client and data/human-games
    # (platform/macos/session.sb). Its rules match real paths, without symlinks.
    cd "$ROOT"
    exec sandbox-exec -D RUNTIME="$ROOT/.runtime" -D CLIENT="$CLIENT" \
      -D GAMES="$ROOT/data/human-games" \
      -f "$ROOT/platform/macos/session.sb" \
      env HOME="$ROOT/.runtime/home" WINEPREFIX="$ROOT/.runtime/wine" \
          GWENT_WINE_ROOT="$WINE_ROOT" GWENT_SANDBOX=sandbox-exec \
      "$ROOT/.venv/bin/python" "$GWENT_SESSION_PROGRAM" "$@"
fi

# Wine: GWENT_WINE_ROOT, else a copy in .local, else the distribution's (the prefix above its bin/wine).
if [[ -n "${GWENT_WINE_ROOT:-}" ]]; then
    WINE_ROOT="$GWENT_WINE_ROOT"
elif [[ -x "$ROOT/.local/wine-fedora/usr/bin/wine64" ]]; then
    WINE_ROOT="$ROOT/.local/wine-fedora/usr"
elif command -v wine >/dev/null; then
    WINE_ROOT="$(dirname "$(dirname "$(readlink -f "$(command -v wine)")")")"
else
    echo 'run-local: Wine not found; see the install guide' >&2
    exit 1
fi
export GWENT_WINE_ROOT="$WINE_ROOT"
export GWENT_PROJECT_ROOT="$ROOT"
# All network and hostname changes are confined to child namespaces.
# GWENT_EPHEMERAL=1 keeps every write out of the tree, except to the directories
# in GWENT_EPHEMERAL_KEEP (separated by colons).
# An optional local hook, started with the session.
if [[ -x "$ROOT/.runtime/hooks/session-start" ]]; then
    nohup "$ROOT/.runtime/hooks/session-start" >> "$ROOT/.runtime/hooks/session-start.out" 2>&1 &
fi

exec unshare --user --map-root-user --net bash -c '
    set -euo pipefail
    ip link set lo up
    # The client may be a symlink (a checkout that links it from another
    # one); bwrap mounts onto the real directory only.
    client="$(readlink -f "$(dirname "$GWENT_PROJECT_ROOT")/client")"
    writable=("$GWENT_PROJECT_ROOT/.runtime" "$client")
    mounts=()
    for dir in "${writable[@]}"; do
        if [[ -n "${GWENT_EPHEMERAL:-}" ]]; then
            # Private copy-on-write view, discarded on exit, so several
            # sessions can share one prefix, client and database.
            mounts+=(--overlay-src "$dir" --tmp-overlay "$dir")
        else
            mounts+=(--bind "$dir" "$dir")
        fi
    done
    if [[ -n "${GWENT_EPHEMERAL:-}" ]]; then
        IFS=: read -ra keep <<< "${GWENT_EPHEMERAL_KEEP:-}"
        for dir in "${keep[@]}"; do
            mounts+=(--bind "$dir" "$dir")
        done
    else
        # Practice games are recorded in data/human-games.
        mounts+=(--bind "$GWENT_PROJECT_ROOT/data/human-games" "$GWENT_PROJECT_ROOT/data/human-games")
    fi
    exec bwrap --ro-bind / / --dev-bind /dev /dev --tmpfs /home --tmpfs /opt --tmpfs /tmp \
      --ro-bind /tmp/.X11-unix /tmp/.X11-unix \
      --ro-bind "$(dirname "$client")" "$(dirname "$client")" \
      "${mounts[@]}" \
      --ro-bind "$GWENT_WINE_ROOT" /opt/gwent-wine \
      --ro-bind "$GWENT_PROJECT_ROOT/.runtime/hosts" /etc/hosts \
      --ro-bind "$GWENT_PROJECT_ROOT/.runtime/resolv.conf" "$(readlink -f /etc/resolv.conf)" \
      --unshare-pid --proc /proc --cap-add CAP_NET_BIND_SERVICE --die-with-parent \
      --setenv HOME "$GWENT_PROJECT_ROOT/.runtime/home" \
      --setenv WINEPREFIX "$GWENT_PROJECT_ROOT/.runtime/wine" \
      --setenv GWENT_WINE_ROOT /opt/gwent-wine \
      --setenv GWENT_SANDBOX bwrap \
      --chdir "$GWENT_PROJECT_ROOT" \
      "$GWENT_PROJECT_ROOT/.venv/bin/python" "$GWENT_SESSION_PROGRAM" "$@"
' bash "$@"
