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

https://github.com/user-attachments/assets/1965eb60-be3c-4b64-966a-d2e328a0172e

## Support the project

If it gave you a good game, buy me a coffee. It helps make the next version
stronger.

<a href="https://buymeacoffee.com/alexbrittaq"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" width="217"></a>

## Why

The community has kept the 2018 public beta alive: players saved the
client, built a community server, and made it run on modern systems. What was missing was a good
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

This is still an early release. The AI won 30 out of 30 games against the
built-in AI—a 100% win rate in that test. It can punish mistakes and
experimental play, but you can still beat it with a strong deck you know
well.

## Play against it

[Download the latest release](https://github.com/brittain9/gwent-ai/releases/latest).
The ZIP includes the mod DLL, matching search workers, and deck roster.
See the [release notes](results/v0.1.0.md).

1. Extract `gwent-ai-v0.1.0.zip`. Put your own client 0.9.24.3.432 in a folder
   named `client` beside the extracted `gwent-ai` folder.
2. Follow the guide for [Linux](docs/install/linux.md),
   [macOS](docs/install/macos.md), or [Windows](docs/install/windows.md)
   to install the prerequisites and connect the community server.
3. Start the game and choose **Single-player, then Practice**. On macOS,
   turn premium cards off in the game's settings: their animations do not
   render on a Mac, and the cards show no art.

Quick start on Linux and macOS, from the extracted `gwent-ai` folder:

```sh
scripts/setup.sh --play --prebuilt  # downloads what it needs and prepares your client
scripts/run-local.sh                # starts the server, game, and search workers
```

You need your own client. On macOS (Apple silicon) you also need only
Rosetta 2; setup downloads the rest. Linux needs Mono 6, Python 3.10+ with
uv, Wine, nginx, OpenSSL, bubblewrap, unprivileged user namespaces, and a
Vulkan driver. The platform guides cover the details.

Prefer to build from source? Follow the [Linux build steps](docs/install/linux.md#build-from-source)
or [macOS build steps](docs/install/macos.md#build-from-source).

[Settings](docs/install/options.md) include the think time.
[Architecture](docs/install/architecture.md) explains how the code is organised.

Release 0.1.0 is tested on macOS (Apple silicon). Linux (Fedora) ran
during development and is not retested for this release. Not tested on
Windows yet.

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
third-party notices: [LICENSE](LICENSE), [NOTICE.md](NOTICE.md).

The community server is by Somnia41205 (MIT). Community deck lists are
credited to their authors where used. The mod loader is MelonLoader
(Apache-2.0), not included.
