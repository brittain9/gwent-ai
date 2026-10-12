# Changelog

Notable changes to the project. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-10-12

The same AI as 0.1.0; this release is about installing and running it.

### Added

- Native Windows support, tested on Windows 11: `scripts\setup.cmd` once,
  then `scripts\run-local.cmd`. No administrator rights, and the same AI
  with the same search workers as Linux and macOS (on .NET 10).
- `CHANGELOG.md`.

### Changed

- The local server is now maintained in this project. Setup downloads
  nothing from the original server project, and the server files that
  depend on the game are generated from your own client.
- Each install makes its own certificate for the local server; no
  certificate is shared between installs.

### Fixed

- Windows: the AI disabled itself after setup ("could not load its local
  card data") because the card data was written with Windows line endings
  ([#5](https://github.com/brittain9/gwent-ai/issues/5)).

### Removed

- The original server's prebuilt launchers, which the previous Windows
  guide used. They added a certificate to Windows' trusted roots and changed
  DNS settings; this project's setup does neither. If you used them, you can
  remove that certificate: open `certlm.msc` as administrator, then
  Trusted Root Certification Authorities, Certificates, and delete the one
  issued to `*.gog.com`.

## [0.1.0] - 2026-10-11

First release: the downloadable mod, matching search workers, the deck
roster and source. Tested on macOS (Apple silicon).
