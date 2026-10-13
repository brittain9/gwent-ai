#!/usr/bin/env bash
# Prepare this checkout after you extract the client to ../client
# (see the install guide).
#
#   scripts/setup.sh          the AI and the headless host (Mono)
#   scripts/setup.sh --play   also Play: Practice in the real client (Wine)
#   scripts/setup.sh --play --prebuilt   install the downloaded release
#
# Linux and macOS (Apple silicon). See the install guide.
#
# 1. Checks prerequisites. For anything missing it prints the install
#    command for your system; it never installs system packages and
#    never needs root.
# 2. Downloads pinned third-party files into .local/ and checks SHA-256.
#    On macOS these include Mono, nginx, uv and Python.
# 3. Builds the AI and the headless host (with --play: everything).
# 4. Reads deck data from your client into .runtime/decks.
# 5. With --play: installs the mod and decks into your client and makes the
#    server's files from it (scripts/prepare-local.py).
#
# It writes only inside this checkout and, with --play, adds files to the
# client. Run it again at any time: finished steps are skipped.
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CLIENT="$(dirname "$ROOT")/client"
LOCAL="$ROOT/.local"

PLAY=0
PREBUILT=0
for option in "$@"; do
  case "$option" in
    --play) PLAY=1 ;;
    --prebuilt) PREBUILT=1 ;;
    *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
  esac
done

MACOS=0
[[ "$(uname -s)" == Darwin ]] && MACOS=1

# Pinned downloads. MelonLoader's hash matches the official release asset;
# DXVK's matches the digest GitHub publishes for the asset.
MELONLOADER_URL=https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip
MELONLOADER_SHA256=147d79e2d61ffd4d1da2811162478d3e22764bc2c5c93fa2c7f0e47dc792b4d0
DXVK_VERSION=2.7.1
DXVK_URL=https://github.com/doitsujin/dxvk/releases/download/v$DXVK_VERSION/dxvk-$DXVK_VERSION.tar.gz
DXVK_SHA256=d85ce7c79f57ecd765aaa1b9e7007cb875e6fde9f6d331df799bce73d513ce87
# macOS: Gcenx's Wine build (x86_64, under Rosetta 2) and his DXVK port to
# MoltenVK, which that Wine bundles. GitHub publishes no digest for the DXVK
# asset; its hash is the one first downloaded.
WINE_MACOS_URL=https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.0_1/wine-stable-11.0_1-osx64.tar.xz
WINE_MACOS_SHA256=b50dc50ec7f41d58b115a6b685d4d1315ba3c797bd3aa0f49213f2703cb82388
DXVK_MACOS_NAME=dxvk-macOS-async-v1.10.3-20230507-repack
DXVK_MACOS_URL=https://github.com/Gcenx/DXVK-macOS/releases/download/v1.10.3-20230507-repack/$DXVK_MACOS_NAME.tar.gz
DXVK_MACOS_SHA256=acd1520ad105d8ef124a09c8e11a259a5dc8bdc565ad18e0e52693f9807b2477
# macOS: Mono (x86_64: the host needs x86_64 float semantics) and nginx are
# prebuilt by platform/macos/build-mono.sh and build-nginx.sh and attached
# to this release. uv is astral's release asset, with the digest GitHub
# publishes for it.
RUNTIME_MACOS=https://github.com/brittain9/gwent-ai/releases/download/v0.3.0
MONO_MACOS_URL=$RUNTIME_MACOS/mono-6.14.1-x86_64-macos.tar.xz
MONO_MACOS_SHA256=f502dc49db0045c72ba3029e50700d73632a9d7a6ec6cdd0d85e53d2e4c0d49b
NGINX_MACOS_URL=$RUNTIME_MACOS/nginx-1.30.5-arm64-macos.tar.xz
NGINX_MACOS_SHA256=363178b062a74d9125b0206abec44e47bc2ba9ebdf1a6ec709c6b13360bcc2ac
UV_MACOS_URL=https://github.com/astral-sh/uv/releases/download/0.10.9/uv-aarch64-apple-darwin.tar.gz
UV_MACOS_SHA256=a92f61e9ac9b0f29668c15f56152e4a60143fca148ff5bfadb86718472c3f376
GWENT_EXE_SHA256=bacc0d9774640213f698ae0400023041f8df5aba753ea595fa3b449206198231
# Read the client's deck definitions (scripts/client_decks.py).
UNITYPY=(--with UnityPy==1.25.3 --with TypeTreeGeneratorAPI==0.0.10)

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
die() { printf 'setup: %s\n' "$*" >&2; exit 1; }

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1
}

if ((MACOS)); then PM=brew
elif command -v dnf >/dev/null; then PM=dnf
elif command -v apt-get >/dev/null; then PM=apt
else PM=other
fi

MISSING_BUILD=()
MISSING_PLAY=()
need() {  # need build|play TOOL DNF_PACKAGE APT_PACKAGE [BREW_PACKAGE]
  local kind=$1 tool=$2 package
  command -v "$tool" >/dev/null && return 0
  case $PM in dnf) package=$3 ;; apt) package=$4 ;; brew) package=${5:-$tool} ;; *) package="$tool" ;; esac
  say "  missing ($kind): $tool"
  if [[ $kind == build ]]; then MISSING_BUILD+=("$package"); else MISSING_PLAY+=("$package"); fi
}
install_hint() {  # names in parentheses were printed with their own command
  set -- $(printf '%s\n' "$@" | grep -v '^(' || true)
  (($#)) || return 0
  case $PM in
    dnf) say "  sudo dnf install $*" ;;
    apt) say "  sudo apt install $*" ;;
    brew) say "  brew install $*" ;;
    *) say "  install: $*" ;;
  esac
}

step "Client"
[[ -f "$CLIENT/Gwent.exe" ]] || die "no client at $CLIENT. Extract Gwent 0.9.24.3.432 there first (see the install guide)."
if [[ "$(sha256 "$CLIENT/Gwent.exe")" == "$GWENT_EXE_SHA256" ]]; then
  say "  $CLIENT: Gwent.exe is 0.9.24.3.432"
else
  say "  warning: $CLIENT/Gwent.exe is not the 0.9.24.3.432 build this project uses"
fi

step "Prerequisites"
if ((!PREBUILT)); then need build dotnet dotnet-sdk-10.0 dotnet-sdk-10.0 dotnet-sdk; fi
# A downloaded release on macOS brings its own uv and Python (.local).
if ((!MACOS || !PREBUILT)); then
  need build python3 python3 python3
  need build uv uv pipx
fi
need build curl curl curl
need build unzip unzip unzip
need build tar tar tar
if ((!PREBUILT)) && command -v dotnet >/dev/null && [[ -z "$(dotnet --list-sdks 2>/dev/null)" ]]; then
  say "  missing: a .NET SDK (only the runtime is installed)"
  MISSING_BUILD+=(dotnet-sdk-10.0)
fi
if ((MACOS)); then
  if ((!PREBUILT)) && ! xcode-select -p >/dev/null 2>&1; then
    say "  missing: the Xcode Command Line Tools (run: xcode-select --install)"
    MISSING_BUILD+=("(Command Line Tools)")
  fi
  if ! arch -x86_64 /usr/bin/true 2>/dev/null; then
    say "  missing: Rosetta 2 (run: softwareupdate --install-rosetta --agree-to-license)"
    MISSING_BUILD+=("(Rosetta 2)")
  fi
else
  need build mono mono-core mono-complete
  if command -v mono >/dev/null && ! mono --version | head -1 | grep -q 'version 6\.'; then
    say "  warning: $(mono --version | head -1); the host is tested with Mono 6.14"
  fi
fi
if ((!MACOS || !PREBUILT)) && command -v python3 >/dev/null \
   && ! python3 -c 'import sys; sys.exit(sys.version_info < (3, 10))'; then
  say "  missing: Python 3.10 or later"
  MISSING_BUILD+=(python3)
fi
if ((MACOS)); then
  # Wine, DXVK and nginx are pinned downloads; the session's sandbox is the
  # system's sandbox-exec (platform/macos/session.sb).
  if ((!PREBUILT)); then
    need play x86_64-w64-mingw32-gcc - - mingw-w64
    need play clang - - "(Command Line Tools)"
  fi
else
  # The Play path can use tools prepared in .local instead of distribution packages.
  [[ -x "$LOCAL/wine-fedora/usr/bin/wine64" ]] || need play wine wine "wine wine64"
  [[ -x "$LOCAL/nginx/sbin/nginx" || -x /usr/sbin/nginx ]] || need play nginx nginx nginx
  if ((!PREBUILT)); then
    [[ -x "$LOCAL/tools/usr/bin/x86_64-w64-mingw32-gcc" ]] || need play x86_64-w64-mingw32-gcc mingw64-gcc gcc-mingw-w64-x86-64
  fi
  need play bwrap bubblewrap bubblewrap
  need play unshare util-linux util-linux
  need play ip iproute iproute2
  need play openssl openssl openssl
  if [[ ! -x /usr/bin/python3 ]]; then
    say "  missing: /usr/bin/python3 (the backend runs on the system Python)"
    MISSING_PLAY+=(python3)
  fi
  if command -v unshare >/dev/null && ! unshare --user --map-root-user --net true 2>/dev/null; then
    say "  missing: unprivileged user namespaces (Play runs the game in its own network namespace)."
    say "  On Ubuntu 24.04 and later, AppArmor blocks them; see the install guide."
    MISSING_PLAY+=("(user namespaces)")
  fi
fi
if ((${#MISSING_BUILD[@]})); then
  say "Build needs:"
  install_hint "${MISSING_BUILD[@]}"
  [[ " ${MISSING_BUILD[*]} " == *" pipx "* ]] && say "  then: pipx install uv   (or see https://docs.astral.sh/uv/)"
  [[ $PM == brew && " ${MISSING_BUILD[*]} " == *" dotnet-sdk "* ]] && say "  (dotnet-sdk is a cask: brew install --cask dotnet-sdk)"
  die "install the Build prerequisites above, then run scripts/setup.sh again."
fi
say "  Build: ready"
if ((${#MISSING_PLAY[@]})); then
  say "  Play needs:"
  install_hint "${MISSING_PLAY[@]}"
  ((PLAY)) && die "install the Play prerequisites above, then run scripts/setup.sh --play again."
else
  say "  Play: ready"
fi

fetch() {  # fetch URL FILE SHA256
  local url=$1 file=$2 sum=$3
  if [[ -f "$file" && "$(sha256 "$file")" == "$sum" ]]; then
    say "  have $(basename "$file") (SHA-256 ok)"
    return
  fi
  mkdir -p "$(dirname "$file")"
  say "  downloading $url"
  curl -fL --retry 3 --progress-bar -o "$file.part" "$url"
  [[ "$(sha256 "$file.part")" == "$sum" ]] || { rm -f "$file.part"; die "SHA-256 mismatch for $url"; }
  mv "$file.part" "$file"
}

if ((PREBUILT)); then
  step "Downloaded release"
  required=(GwentAI.dll host/GwentAI.dll host/GwentHost.exe host/GwentHost.exe.config)
  if ((PLAY)); then required+=(GwentBetaRestorationMod.dll GalaxyCommunication.exe netprobe.exe); fi
  if ((PLAY && MACOS)); then required+=(session_net.dylib); fi
  for file in "${required[@]}"; do
    [[ -f "$ROOT/dist/$file" ]] || die "dist/$file is missing; extract the complete release again."
  done
  [[ "$(sha256 "$ROOT/dist/GwentAI.dll")" == "$(sha256 "$ROOT/dist/host/GwentAI.dll")" ]] \
    || die "the AI and its search workers differ; extract the complete release again."
fi

step "Downloads (.local)"
fetch "$MELONLOADER_URL" "$LOCAL/downloads/MelonLoader.x64.zip" "$MELONLOADER_SHA256"
# Build references (MelonLoader.dll, 0Harmony.dll) and the host's run-time
# dependencies come from the extracted loader.
if [[ ! -f "$LOCAL/melonloader/MelonLoader/0Harmony.dll" ]]; then
  rm -rf "$LOCAL/melonloader.part"
  unzip -q "$LOCAL/downloads/MelonLoader.x64.zip" -d "$LOCAL/melonloader.part"
  mv "$LOCAL/melonloader.part" "$LOCAL/melonloader"
  say "  extracted MelonLoader to .local/melonloader"
fi
extract() {  # extract URL SHA256 DIR: a tarball whose top directory is DIR, into .local/DIR
  local file="$LOCAL/downloads/${1##*/}"
  if [[ -e "$LOCAL/$3" ]]; then say "  have .local/$3"; return; fi
  fetch "$1" "$file" "$2"
  rm -rf "$LOCAL/$3.part"
  mkdir -p "$LOCAL/$3.part"
  tar -xf "$file" -C "$LOCAL/$3.part"
  mv "$LOCAL/$3.part/$3" "$LOCAL/$3"
  rm -rf "$LOCAL/$3.part"
  say "  extracted .local/$3"
}
UV=uv
if ((MACOS)); then
  extract "$MONO_MACOS_URL" "$MONO_MACOS_SHA256" mono-x86_64
  # uv and the Python it installs stay in .local, and uv never runs the
  # Mac's /usr/bin/python3, which asks to install the Command Line Tools.
  if [[ ! -x "$LOCAL/uv/uv" ]]; then
    fetch "$UV_MACOS_URL" "$LOCAL/downloads/uv-aarch64-apple-darwin.tar.gz" "$UV_MACOS_SHA256"
    mkdir -p "$LOCAL/uv"
    tar -xzf "$LOCAL/downloads/uv-aarch64-apple-darwin.tar.gz" -C "$LOCAL/uv" --strip-components=1
    say "  extracted .local/uv"
  fi
  UV="$LOCAL/uv/uv"
  export UV_PYTHON_PREFERENCE=only-managed UV_PYTHON_INSTALL_DIR="$LOCAL/python" UV_CACHE_DIR="$LOCAL/uv-cache"
fi
if ((PLAY && MACOS)); then
  extract "$NGINX_MACOS_URL" "$NGINX_MACOS_SHA256" nginx
  if [[ ! -x "$LOCAL/wine-macos/bin/wine" ]]; then
    fetch "$WINE_MACOS_URL" "$LOCAL/downloads/wine-stable-11.0_1-osx64.tar.xz" "$WINE_MACOS_SHA256"
    rm -rf "$LOCAL/wine-macos.part"
    mkdir -p "$LOCAL/wine-macos.part"
    tar -xJf "$LOCAL/downloads/wine-stable-11.0_1-osx64.tar.xz" -C "$LOCAL/wine-macos.part" \
      --strip-components=4 'Wine Stable.app/Contents/Resources/wine'
    mv "$LOCAL/wine-macos.part" "$LOCAL/wine-macos"
    say "  extracted Wine to .local/wine-macos"
  else
    say "  have .local/wine-macos"
  fi
  if [[ ! -f "$LOCAL/dxvk-macos/x86_64-windows/d3d11.dll" ]]; then
    fetch "$DXVK_MACOS_URL" "$LOCAL/downloads/$DXVK_MACOS_NAME.tar.gz" "$DXVK_MACOS_SHA256"
    rm -rf "$LOCAL/dxvk-macos.part"
    mkdir -p "$LOCAL/dxvk-macos.part"
    tar -xzf "$LOCAL/downloads/$DXVK_MACOS_NAME.tar.gz" -C "$LOCAL/dxvk-macos.part"
    mkdir -p "$LOCAL/dxvk-macos"
    mv "$LOCAL/dxvk-macos.part/$DXVK_MACOS_NAME/x64" "$LOCAL/dxvk-macos/x86_64-windows"
    mv "$LOCAL/dxvk-macos.part/$DXVK_MACOS_NAME/x32" "$LOCAL/dxvk-macos/i386-windows"
    rm -rf "$LOCAL/dxvk-macos.part"
    say "  extracted DXVK-macOS to .local/dxvk-macos"
  else
    say "  have .local/dxvk-macos"
  fi
elif ((PLAY)); then
  # Any DXVK already in .local/dxvk is kept.
  if [[ ! -f "$LOCAL/dxvk/x86_64-windows/d3d11.dll" ]]; then
    fetch "$DXVK_URL" "$LOCAL/downloads/dxvk-$DXVK_VERSION.tar.gz" "$DXVK_SHA256"
    rm -rf "$LOCAL/dxvk.part"
    mkdir -p "$LOCAL/dxvk.part"
    tar -xzf "$LOCAL/downloads/dxvk-$DXVK_VERSION.tar.gz" -C "$LOCAL/dxvk.part"
    mkdir -p "$LOCAL/dxvk"
    mv "$LOCAL/dxvk.part/dxvk-$DXVK_VERSION/x64" "$LOCAL/dxvk/x86_64-windows"
    mv "$LOCAL/dxvk.part/dxvk-$DXVK_VERSION/x32" "$LOCAL/dxvk/i386-windows"
    rm -rf "$LOCAL/dxvk.part"
    say "  extracted DXVK $DXVK_VERSION to .local/dxvk"
  else
    say "  have .local/dxvk"
  fi
fi
if ((PLAY || (MACOS && PREBUILT))); then
  step "Python environment for the backend (.venv)"
  if ((MACOS)); then
    # A pinned version, which uv downloads if this Mac lacks it.
    [[ -x "$ROOT/.venv/bin/python" ]] || "$UV" venv --quiet --python 3.12 "$ROOT/.venv"
  # The Linux sandbox (run-local.sh) hides /home, /opt and /tmp, so the
  # backend's interpreter must be the system one under /usr.
  elif [[ "$(readlink -f "$ROOT/.venv/bin/python" 2>/dev/null)" != /usr/* ]]; then
    rm -rf "$ROOT/.venv"
    uv venv --quiet --python /usr/bin/python3 "$ROOT/.venv"
  fi
  "$UV" pip install --quiet --python "$ROOT/.venv/bin/python" -r "$ROOT/server/requirements.txt"
  say "  .venv: $(cd "$ROOT" && .venv/bin/python -c 'import websockets; print("websockets", websockets.__version__)')"
fi

PY=python3
[[ -x "$ROOT/.venv/bin/python" ]] && PY="$ROOT/.venv/bin/python"
if ((PREBUILT)); then
  step "Card data from your client"
  "$PY" "$ROOT/scripts/install-card-data.py" --client "$CLIENT" --out "$LOCAL/runtime-cards"
else
  step "Build"
  if ((PLAY)); then "$ROOT/scripts/build-local.sh"; else "$ROOT/scripts/build-local.sh" host; fi
fi

step "Decks from your client (.runtime/decks)"
# CDPR's own deck definitions: the seven Practice opponents and the client's
# low-level decks. They stay out of Git, so each checkout reads them here.
if compgen -G "$ROOT/.runtime/decks/practice/*.json" >/dev/null \
   && [[ $(ls "$ROOT/.runtime/decks/practice"/*.json | wc -l) -eq 7 ]]; then
  say "  have the 7 Practice decks"
else
  (cd "$ROOT" && "$UV" run --quiet --no-project --python "$PY" "${UNITYPY[@]}" python scripts/client_decks.py)
  "$PY" "$ROOT/scripts/roster.py" import-client
fi

if ((PLAY)); then
  step "Your client"
  "$PY" "$ROOT/scripts/prepare-local.py"
fi

step "Done"
if ((PLAY)); then
  say "Play:     scripts/run-local.sh (see the install guide)"
else
  say "Play:     scripts/setup.sh --play"
fi
