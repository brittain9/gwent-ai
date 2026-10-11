#!/usr/bin/env bash
# Builds Mono for the headless host on macOS: x86_64, run by Rosetta 2.
#
#   platform/macos/build-mono.sh SOURCE_TARBALL PREFIX
#
# Why x86_64: the host must evaluate float arithmetic in double precision, as
# Unity's Mono in the client does (-O=-float32, GwentHost/Program.cs). Mono's
# arm64 JIT rounds every float step to single precision whatever the flags,
# so the arm64 Mono that Homebrew ships plays different games.
#
# Needs the Xcode Command Line Tools and Rosetta 2. About 20 minutes on an
# M2 Pro; scripts/setup.sh runs it once.
set -euo pipefail
SOURCE=$1 PREFIX=$2
SDK="$(xcrun --show-sdk-path)"
# The real compiler, assembler and linker (Command Line Tools or Xcode).
TOOLS="$(dirname "$(xcrun --find clang)")"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/gwent-mono.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

tar -xJf "$SOURCE" -C "$WORK" --strip-components=1
cd "$WORK"
export CC="$TOOLS/clang -arch x86_64 -isysroot $SDK"
export CXX="$TOOLS/clang++ -arch x86_64 -isysroot $SDK"
export SDKROOT="$SDK"
# The build runs the new x86_64 mono, which runs the assembler and linker
# while it AOT-compiles the class libraries. /usr/bin/{as,ld,clang} are xcrun
# shims whose library is arm64-only and fails to load from an x86_64 parent,
# so the real tools come first.
export PATH="$TOOLS:/usr/bin:/bin:/usr/sbin:/sbin"
LOG="$PREFIX.build.log"
echo "  building Mono into $PREFIX (log: $LOG)"
# It installs into a staging tree and moves into PREFIX only when complete,
# so an interrupted build never leaves a Mono that setup.sh takes as built.
STAGE="$WORK/stage"
if ! { ./configure --prefix="$PREFIX" --build=x86_64-apple-darwin --host=x86_64-apple-darwin \
         --disable-nls --disable-btls --with-mcs-docs=no &&
       make -j "$(sysctl -n hw.ncpu)" &&
       make install DESTDIR="$STAGE"; } > "$LOG" 2>&1; then
  tail -20 "$LOG" >&2
  exit 1
fi
rm -rf "$PREFIX"
mv "$STAGE$PREFIX" "$PREFIX"
rm -f "$LOG"
