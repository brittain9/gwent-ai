# Changelog

Notable changes to the project. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-10-18

Same AI as 0.3.0, with emotes, arena mode and a Windows setup fix.

### Added

- Emotes: the opponent uses the game's taunts like a person. Each game it
  draws a persona: about half the games it stays silent, most of the rest
  a taunt or two, and one game in ten it is chatty, up to five. It reacts
  to a round lost narrowly (Curses), a round it threw away after leading
  (Well played), a helpful pass by you (Thanks), a big gold card about to
  be played (Watch this), a round-3 lead with a card in hand (a threat) and
  a lost game (Well played; nothing after a win). At least three of its own
  decisions pass between taunts, and a muted opponent sends nothing. On by
  default; set `GWENT_AI_EMOTES=0` before starting the session to turn it
  off.
- Arena, an early version: the game's own arena screens work against this
  AI. The local server gives a free ticket, a draft of 26 cards (four offers
  a pick, one tier per pick in a shuffled mix of 9 gold, 8 silver and 9
  bronze, any faction, no card offered twice) and then a leader from four.
  A run ends at 9 wins or 3 losses. Each match is a local game against the
  AI, which drafts its own deck at random from the same offers and plays it
  with its full search and the emotes. The local server saves progress, so
  a run resumes after a restart. On by default; set `GWENT_AI_ARENA=0` to
  turn it off.

### Fixed

- Windows: setup on a Python without a prebuilt lz4 wheel failed with a
  compiler error ("Failed building wheel for lz4"). Setup now checks for a
  supported Python (3.10 to 3.14, 64-bit), installs prebuilt wheels only,
  and prints a plain message otherwise.

### Known limits

Arena is early: the AI picks its draft at random, so its arena decks have
none of the synergy of its Practice decks and it is much easier to beat
there. A later release can teach it to draft for a plan. There is no
rewards screen after a run; the game's rewards screen may wait and show a
timeout. You get one new ticket each time the game starts, so restart the
client for the next run. Emotes and arena were tested on Linux and macOS
(Apple silicon), not yet on Windows.

## [0.3.0] - 2026-10-13

A trained value network judges positions from round 2 on. Wins 56% of the
points (95% range 52% to 60%), +40 Elo (+12 to +68), against 0.2.0 over 490 games (245 seat-swapped pairs) at 6,000
simulations a move, 13 October 2026; details in
[results/v0.3.0.md](results/v0.3.0.md).

### Added

- A value network (`data/model/fta.vnet`) that judges the positions where the
  search stops looking ahead, from round 2 on. Round 1 keeps the simpler
  scorer.

### Fixed

- The AI decides only after the game has applied the actions a card's effect
  had pending, so its search workers see the position it decides from.
- Card tables with Windows line endings are accepted.

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
