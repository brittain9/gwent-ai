#!/usr/bin/env bash
# Fetch the files this project does not keep: the nginx static pages, the
# game's config/news/shop/prices responses and deploy/rewards.json. They are
# derived from the game's online services, so they come from upstream's
# repository (Somnia41205's private server) at the pinned commit, and
# every file is checked against upstream-files.sha256 before it is kept.
# Files already present with the right hash are skipped; rerunning is safe.
set -euo pipefail

SERVER_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMMIT=8295512683514fd4e6c79168d9f129716aaaf8d4
BASE="https://raw.githubusercontent.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server/$COMMIT"
MANIFEST=upstream-files.sha256

cd "$SERVER_DIR"

sha256() {
    if command -v sha256sum >/dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

hash_of() { sha256 "$1" | cut -d' ' -f1; }

while read -r expected path; do
    if [ -f "$path" ] && [ "$(hash_of "$path")" = "$expected" ]; then
        echo "ok       $path"
        continue
    fi
    mkdir -p "$(dirname "$path")"
    part="$path.part"
    curl -fsSL "$BASE/$path" -o "$part" || {
        rm -f "$part"
        echo "DOWNLOAD FAILED $path" >&2
        exit 1
    }
    actual="$(hash_of "$part")"
    if [ "$actual" != "$expected" ]; then
        rm -f "$part"
        echo "MISMATCH $path: expected $expected, got $actual" >&2
        exit 1
    fi
    mv -f "$part" "$path"
    echo "fetched  $path"
done < "$MANIFEST"

sha256 --quiet -c "$MANIFEST"
echo "all files in $MANIFEST verified"
