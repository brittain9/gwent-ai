# Gwent AI

**A strong, fair opponent for the 2018 Gwent public beta
(client 0.9.24.3.432), for the game's Practice mode.**

You play it in the real game client. It uses the game's own rules engine, so
every card works the way it did in 2018. It plays fair: it sees only what a
human in its seat would see, and it guesses your hand and deck from the
cards it has seen.

This is a hobby project. It is not affiliated with CD PROJEKT RED. Gwent,
its client, cards and art belong to CD PROJEKT RED, and none of them is in
this project.

## Why

The 2018 public beta was, for many players, the best version of Gwent, and
the community kept it alive: players saved the client, built a community
server and made it run on modern systems. What was missing was a good
opponent. The built-in AI is easy, it knows seven decks, and it can see your
hand. This project replaces it.

## How it plays

- **Search.** Before each move it runs a Monte Carlo Tree Search: it
  imagines thousands of possible continuations of the game with the real
  rules, and picks the move that did best across them.
- **Hidden cards.** Your hand is hidden, so each imagined game first fills
  in a guess for it. The variant used is information-set MCTS: one search
  tree over what the AI actually knows, so it never plans with knowledge it
  does not have.
- **Belief.** The guesses come from a belief model. A deck posterior says
  which known 2018 deck you are most likely playing, given the cards seen so
  far. An opponent model, fitted on recorded games, makes the imagined
  opponent keep its gold cards and answer threats the way real players do.
- **Learned parts.** A small policy network suggests which moves to try
  first. A small logistic model, fitted on games, scores each position at
  the end of the current round as a chance to win the match.
- **Fair play.** It sees only what a human in its seat would see, and it
  uses the game's own rules engine, so every card works as it did in 2018.
- **Decks and time.** It plays the decks of the 2018 ladder, a different one
  each match, and thinks for about 10 seconds on a normal move and up to 30
  on a hard one. More processor cores make it stronger.

## How strong

An earlier version won 30 of 30 games against the game's built-in AI, at the
built-in AI's own thinking time, with the same strong decks on both sides
(7 October 2026, 30 games). The measured numbers of each release, with
their sample sizes and dates, come with the release in `results/`.

## Play against it

Release 1 is source with a build script: you build the AI against your own
client. Its notes are in `results/v1.md`. You will need:

- Your own copy of the client 0.9.24.3.432 (June 2018). The install guide
  gives its file hashes so that you can check your copy.
- MelonLoader 0.5.7 in the client folder; the setup script downloads it.
- A community server for the beta. On Linux and macOS, the project's copy
  of Somnia41205's unofficial server (MIT). On Windows, upstream's launcher.
- To build the AI against your client: the .NET SDK, Mono 6, and Python 3.10
  or newer with uv. The build reads your client for the card tables it
  needs; nothing from the game is in the project.
- On Linux and macOS: Wine, nginx, MinGW-w64 and openssl; on Linux also
  bubblewrap and unprivileged user namespaces, and a Vulkan driver for DXVK.

Quick start on Linux and macOS, from the project folder with the client
beside it:

```sh
scripts/setup.sh --play      # checks prerequisites, downloads the pinned files, builds the AI
server/fetch-upstream.sh     # once: the community server's upstream service files
scripts/prepare-local.py     # installs MelonLoader, the AI and its decks into the client
scripts/run-local.sh         # starts the server, the game under Wine and the search workers
```

Then choose **Single-player, then Practice**. The guides:
`docs/install/linux.md`, `docs/install/macos.md`, `docs/install/windows.md`.
Settings such as the think time: `docs/install/options.md`. How the code
is organised: `docs/install/architecture.md`.

Tested on Linux (Fedora, with Wine). macOS with Wine on Apple silicon is
documented. Not tested on Windows yet.

## What is in the project

| Part | What it is |
|---|---|
| Source code and model files | The AI: the search, the guesses about hidden cards, the learned move suggestions, the helper processes |
| Community server | Our copy of Somnia41205's unofficial server for the beta (MIT), with its restoration mod, built from source |
| Deck roster | The 2018 ladder decks the AI plays and guesses from, credited to their authors |
| Results | The measured numbers of each release, with sample sizes and dates |
| Install steps | How to run the AI against your own copy of the game |

## Tell us how it played

Open an issue titled "Game report": your deck, the opponent's deck, the
round and the turn where the AI did something odd, and what you would have
played. A pass at the wrong time, an overcommit, or a card used badly are
the most useful reports. To attach the game itself, run
`scripts/game-report.py --pack`: it packs your last Practice game into a
zip for the issue. The zip holds the decks as card ids, the moves, the
scores, the deal's seed, the AI's settings, search statistics and timings,
the start time in UTC and the number of search workers. It holds no deck
name you typed, no account and no path. Do not send anything from the
game's own files. We read every report. Fixes to the
install steps and the docs are welcome. Changes to how the AI plays are made
after head-to-head measurement, so we do not take code for them.

## Licence and credits

Code and models: PolyForm Noncommercial 1.0.0. In plain words: run it,
study it, change it and share it for noncommercial purposes, and keep the
notice; do not sell it or use it commercially. Documents: CC BY-NC-ND 4.0.
Share your games, screenshots and videos freely, with a link. Details and
third-party notices: `LICENSE`, `NOTICE.md`.

The community server is by Somnia41205 (MIT). Community deck lists are
credited to their authors where used. The mod loader is MelonLoader
(Apache-2.0), not included.
