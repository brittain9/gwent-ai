#!/usr/bin/env bash
# Builds a self-contained nginx for macOS (Apple silicon). It makes the
# prebuilt nginx that scripts/setup.sh downloads:
#   platform/macos/build-nginx.sh "$PWD/nginx"
#   tar -cJf nginx-1.30.5-arm64-macos.tar.xz nginx
#
# Why: players should not need Homebrew's nginx. OpenSSL and PCRE2 are linked
# statically, so the binary needs only system libraries. It has the modules
# server/host_nginx.conf uses (ssl, v2, proxy, rewrite, headers) and the
# defaults; the optional ones are off. The nginx version matches
# scripts/build-local.sh.
#
# Result: OUTPUT_DIR/sbin/nginx and OUTPUT_DIR/licenses/. Needs the Xcode
# Command Line Tools and network access. About a minute.
set -euo pipefail
OUT=$1
NGINX_VERSION=1.30.5
OPENSSL_VERSION=3.5.9   # LTS, supported to 2030-04
PCRE2_VERSION=10.49
# OpenSSL: published by upstream (.sha256 beside the tarball).
# PCRE2: the digest GitHub shows for the release asset.
# nginx publishes only PGP signatures: this hash is from the first download.
NGINX_SHA256=6c20565aa2325cb82216ae804f4a4ff1875179014759a381c42ddc8e11c4906d
OPENSSL_SHA256=603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a
PCRE2_SHA256=53c156e1ba416a20da8e65395daa132da0d80e76910424caca3fcdae7831d384

WORK="$(mktemp -d "${TMPDIR:-/tmp}/gwent-nginx.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

fetch() {  # URL SHA256 DIRNAME
  local file="${1##*/}"
  curl -fsSL -o "$file" "$1"
  echo "$2  $file" | shasum -a 256 -c - >/dev/null
  mkdir "$3"
  tar -xf "$file" -C "$3" --strip-components=1
}
fetch "https://nginx.org/download/nginx-$NGINX_VERSION.tar.gz" "$NGINX_SHA256" nginx
fetch "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VERSION/openssl-$OPENSSL_VERSION.tar.gz" "$OPENSSL_SHA256" openssl
fetch "https://github.com/PCRE2Project/pcre2/releases/download/pcre2-$PCRE2_VERSION/pcre2-$PCRE2_VERSION.tar.bz2" "$PCRE2_SHA256" pcre2

# Only the system toolchain: a Homebrew compiler or library must not leak in.
export PATH="/usr/bin:/bin:/usr/sbin:/sbin"
LOG="$WORK/build.log"
echo "  building nginx $NGINX_VERSION into $OUT (log: $LOG)"
if ! { cd nginx &&
       ./configure --prefix="$OUT" \
         --with-cc=/usr/bin/clang \
         --with-http_ssl_module --with-http_v2_module \
         --with-openssl="$WORK/openssl" --with-openssl-opt="no-shared no-tests" \
         --with-pcre="$WORK/pcre2" \
         --without-http_gzip_module --without-http_upstream_zone_module &&
       make -j "$(sysctl -n hw.ncpu)" &&
       mkdir -p "$OUT/sbin" "$OUT/licenses" &&
       cp objs/nginx "$OUT/sbin/nginx" &&
       cp LICENSE "$OUT/licenses/nginx-LICENSE" &&
       cp ../openssl/LICENSE.txt "$OUT/licenses/openssl-LICENSE.txt" &&
       cp ../pcre2/LICENCE.md "$OUT/licenses/pcre2-LICENCE.md"; } > "$LOG" 2>&1; then
  tail -20 "$LOG" >&2
  exit 1
fi
