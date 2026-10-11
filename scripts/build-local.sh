#!/usr/bin/env bash
# Build from source:
#   scripts/build-local.sh        everything: the AI and headless host, the
#                                 restoration mod, the Galaxy helper, nginx
#   scripts/build-local.sh host   the AI and headless host only
# Where .NET 10 is installed (Linux), both also build the host on .NET
# (GwentHost.Core, dist/host-core): GWENT_WORKER_RUNTIME=coreclr runs workers on it.
# Tools: distribution packages, or copies you placed in .local
# (MinGW in .local/tools, nginx source in .local/nginx-*).
set -euo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
TOOLS="$ROOT/.local/tools/usr"
GCC_VERSION=16.2.1
NGINX_VERSION=1.30.5
export DOTNET_CLI_HOME="$ROOT/.local/dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p dist

# Card tables and runtime data come from the user's client.
# A build writes only .local; setup installs the runtime files into the client.
if python3 -c 'import sys; sys.path.insert(0, "scripts"); import clientdata; sys.exit(not clientdata.DEFINITIONS.exists())'; then
  [[ -f data/cards/roles.json ]] || python3 scripts/card_roles.py
  python3 scripts/answer_kinds.py
  card_client="$(python3 -c 'import sys; sys.path.insert(0, "scripts"); import clientdata; print(clientdata.CLIENT)')"
  python3 scripts/install-card-data.py --client "$card_client" --out "$ROOT/.local/runtime-cards"
fi
# GwentHost builds the GwentAI mod too (dist/GwentAI.dll, dist/host/).
dotnet build GwentHost/GwentHost.csproj -c Release --ignore-failed-sources
# The build stamp: the commit and whether tracked files have uncommitted
# changes (game records do not count as changes).
if git rev-parse HEAD >/dev/null 2>&1; then
  if [[ -n "$(git status --porcelain --untracked-files=no -- ':(top)' \
        ':(top,exclude)data/*-games')" ]]; then
    dirty=true
  else
    dirty=false
  fi
  printf '{"commit": "%s", "dirty": %s, "built": "%s"}\n' "$(git rev-parse HEAD)" "$dirty" "$(date -Iseconds)" \
    > dist/host/build.json
fi
if [[ "$(uname -s)" == Linux ]] && dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 10\.'; then
  # The first build restores HarmonyX and System.Security.Permissions from
  # NuGet (network once). Same GwentAI.dll and stamp as dist/host. Without
  # it the workers run on Mono (local_session.py compares the GwentAI.dll).
  if dotnet build GwentHost.Core/GwentHost.Core.csproj -c Release; then
    cp dist/host/build.json dist/host-core/build.json
  else
    echo 'build-local: GwentHost.Core did not build; search workers will run on Mono' >&2
  fi
fi
# Keep each frozen host with its matching local card data.
for host_dir in dist/host dist/host-core; do
  if [[ -d "$host_dir" && -d .local/runtime-cards ]]; then
    mkdir -p "$host_dir/card-data"
    cp .local/runtime-cards/{roles.json,answer-params.json,manifest.json} "$host_dir/card-data/"
  fi
done
# Compare this line with the card table hash in the release notes.
card_table() {
  [[ -f data/cards/roles.json ]] || return 0
  printf 'card table: sha256 %s\n' "$(python3 -c 'import hashlib, sys; print(hashlib.sha256(open(sys.argv[1], "rb").read()).hexdigest()[:16])' data/cards/roles.json)"
}
if [[ "${1:-}" == host ]]; then
  printf '\nBuilt the AI mod and the headless host.\n'
  card_table
  exit 0
fi
dotnet build server/GwentBetaRestorationMod/GwentBetaRestorationMod.csproj -c Release --ignore-failed-sources

if [[ -x "$TOOLS/bin/x86_64-w64-mingw32-gcc" ]]; then
  CC=("$TOOLS/bin/x86_64-w64-mingw32-gcc"
      -B "$TOOLS/libexec/gcc/x86_64-w64-mingw32/$GCC_VERSION/"
      -B "$TOOLS/lib/gcc/x86_64-w64-mingw32/$GCC_VERSION/"
      -B "$TOOLS/x86_64-w64-mingw32/bin/"
      -I "$TOOLS/x86_64-w64-mingw32/sys-root/mingw/include"
      -L "$TOOLS/x86_64-w64-mingw32/sys-root/mingw/lib")
elif command -v x86_64-w64-mingw32-gcc >/dev/null; then
  CC=(x86_64-w64-mingw32-gcc)
else
  echo 'build-local: no MinGW compiler (x86_64-w64-mingw32-gcc); run scripts/setup.sh --play' >&2
  exit 1
fi
"${CC[@]}" -O2 -Wall -Wextra server/comet-main/dummy-service/communication_wine.c \
  -o dist/GalaxyCommunication.exe -ladvapi32 -mwindows -Wl,--no-insert-timestamp
"${CC[@]}" -O2 -Wall -Wextra platform/netprobe.c -o dist/netprobe.exe -lws2_32 -Wl,--no-insert-timestamp
if [[ "$(uname -s)" == Darwin ]]; then
  # Wine on macOS is an x86_64 program (Rosetta 2 on Apple silicon).
  clang -arch x86_64 -O2 -Wall -Wextra -dynamiclib platform/macos/session_net.c \
    -o dist/session_net.dylib
fi

# A distribution nginx needs no build (scripts/local_session.py finds it).
if [[ ! -x "$ROOT/.local/nginx/sbin/nginx" ]] && ! command -v nginx >/dev/null && [[ ! -x /usr/sbin/nginx ]]; then
  if [[ ! -d "$ROOT/.local/nginx-$NGINX_VERSION" ]]; then
    echo 'build-local: no nginx; install your distribution nginx package' >&2
    exit 1
  fi
  (cd "$ROOT/.local/nginx-$NGINX_VERSION"
   ./configure --with-cc=/usr/bin/gcc --prefix="$ROOT/.local/nginx" \
     --with-http_ssl_module --with-http_v2_module
   make -j "${GWENT_BUILD_JOBS:-8}"
   make install)
fi
printf '\nBuilt the AI mod, headless host, restoration mod and Galaxy helper.\n'
card_table
