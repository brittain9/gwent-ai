# Third-Party Components & Attribution

This repository retains third-party source, configuration, and documentation.
Downloaded compiled binaries have been removed. Retained components keep their
original licenses, included alongside them.

## nginx
- License: BSD-2-Clause. See `Nginx/docs/LICENSE`.
- Bundled dependency licenses: `Nginx/docs/OpenSSL.LICENSE`,
  `Nginx/docs/PCRE.LICENCE`, `Nginx/docs/zlib.LICENSE`.
- The imported `Nginx/nginx.exe` has been removed; nginx source is not included.

## comet (GOG Galaxy backend reimplementation)
- License: Apache-2.0. See `comet-main/LICENSE`.
- We include and build comet's `dummy-service`. Modified from upstream:
  `communication_wine.c` and the meson build (Apache-2.0 sec. 4(b)).

## Game data (CD PROJEKT RED)
- `rewards.json` and related game content are property of CD PROJEKT RED and are NOT covered by this project's
  license. Not included: `server/fetch-upstream.sh` fetches them from upstream's repository at a pinned commit,
  so the restoration server can function.
