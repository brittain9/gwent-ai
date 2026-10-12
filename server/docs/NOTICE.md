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
- Game content is property of CD PROJEKT RED and is NOT covered by this project's license, and none of it is
  included. The server's responses that depend on the client (the game version and data secret, the card
  definitions) are made on the player's PC from the player's own client at setup (`scripts/server_static.py`,
  `deploy/setup_local_server.py`); see `UPSTREAM.md`.
