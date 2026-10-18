# Options

Set these environment variables before `scripts/run-local.sh` (for example
`GWENT_AI_THINK_MS=13333 scripts/run-local.sh`). All are optional.

## Play

| Variable | What it does |
|---|---|
| `GWENT_AI_THINK_MS` | The AI's think-time budget per decision, in ms (default 20000 from `run-local.sh`). Clear decisions stop at half of it; open ones may run on to 1.5 times it. 13333 gives about 6.7 s for most plays and 20 s at most. |
| `GWENT_AI_ITERATIONS` | The most simulated games per decision (default 50000). The time budget usually stops the search first. |
| `GWENT_PRACTICE_WORKERS` | The number of search worker processes (default `min(6, cores − 2)`; `--workers N` does the same). `0` searches in the game process only, which makes the AI weaker. |
| `GWENT_WORKER_RUNTIME` | `auto` (default): uses matching .NET 10 workers when available, otherwise Mono. The download uses Mono. `coreclr` requires a matching .NET build; `mono` selects Mono. Both play the same moves. |
| `GWENT_AI_EMOTES` | `1` (default): the opponent uses the game's taunts. `0` turns them off. |
| `GWENT_AI_ARENA` | `1` (default): the game's arena screens play against the AI. `0` turns arena off. |
| `GWENT_PRACTICE_DECKS` | `practice`: the AI plays the client's seven Practice decks instead of the deck roster. |
| `GWENT_WORKER_MEMORY_MB` | A worker exits above this memory (RSS, default 2048) and the session starts it again. |
| `GWENT_WORKER_GC_PARAMS` | Mono's garbage-collector settings for the workers (default `soft-heap-limit=819m,nursery-size=256m`). |

`run-local.sh --stock-ai` plays Practice against the client's own AI
(the session sets `GWENT_STOCK_AI=1`).

## Paths and builds

| Variable | What it does |
|---|---|
| `GWENT_CLIENT` | The client directory the build and the scripts read (default `../client`). |
| `GWENT_WINE_ROOT` | The Wine installation (default: a copy in `.local`, else your distribution's Wine). |
| `GWENT_HOST`, `GWENT_HOST_CORE` | Another build of the headless host (`dist/host/GwentHost.exe`, `dist/host-core/GwentHost.Core.dll`). |
| `GWENT_MONO_FLAGS` | Mono's JIT flags for the host (default `-O=-float32`; see `GwentHost/Program.cs`). |
| `GWENT_BUILD_JOBS` | Parallel jobs when `build-local.sh` builds nginx (default 8). |

## Diagnostics

| Variable | What it does |
|---|---|
| `GWENT_WINE_DEBUG` | Wine's `WINEDEBUG` channels (default `-all`). |
| `GWENT_HOST_DEBUG_LOG` | `1`: the host prints the engine's debug log. |
| `GWENT_PHASE_CLOCK` | A file path: each search process appends the time spent in each search phase to it at exit. |
| `GWENT_ENGINE_PATCHES` | The host's engine speed-ups: `2` (default), `1` (without the pack) or `0` (off). They change no move. |
| `GWENT_ENGINE_PACK` | `name,name`: only these parts of the speed-up pack (default: all). |
| `GWENT_USHORT_SAFE_INDEXOF` | `0` turns off the host's fix for a Mono lookup on 16-bit keys. It changes no move. |

## Several sessions or another session program

| Variable | What it does |
|---|---|
| `GWENT_EPHEMERAL` | `1` (Linux): the session writes to a private copy-on-write view of `.runtime` and the client that is discarded at exit, so several sessions can run at once. Games are not recorded. |
| `GWENT_EPHEMERAL_KEEP` | With `GWENT_EPHEMERAL`: directories, separated by colons, that are still written through. |
| `GWENT_SESSION_PROGRAM` | The Python program that runs the session in the sandbox (default `scripts/local_session.py`); another program can extend its `Session` class. |

The session sets `GWENT_PLAYER`, `GWENT_GAMES_DIR`, `GWENT_PROJECT_ROOT`,
`GWENT_SEARCH_WORKERS` and `GWENT_SANDBOX` itself; do not set them.
