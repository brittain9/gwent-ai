# How the code is organised

The AI is a mod for the Gwent beta client. In Practice it takes the
opponent's seat and plays with a search. The game's own rules engine runs
every simulated game, so the AI plays by the real rules.

| Path | What it holds |
|---|---|
| `GwentAI/` | The mod (`GwentAI.dll`, loaded by MelonLoader) |
| `GwentHost/`, `GwentHost.Core/` | The headless host: the same engine without the game window, on Mono and on .NET |
| `GwentAI.Tests/` | Unit tests of the rules the search uses (`dotnet test`) |
| `scripts/` | Setup, build, the local session and helper tools |
| `server/` | The local server for the client's online services (Somnia41205's community server) |
| `platform/` | Network checks and the macOS sandbox |
| `data/` | The deck roster, the deck population, the round evaluator and the policy network |

## GwentAI/

- **Search** (`GwentAI/Search`): information-set Monte Carlo tree search
  (ISMCTS). The AI cannot see the opponent's hand, so each simulation
  first deals a guess at the hidden cards (a *determinization*) and then
  plays the game out. Many simulations share one tree, so the AI picks the
  play that does best over many guesses.
  - *Belief* (`Belief.cs`): how the guesses are made. The opponent's deck
    is drawn from the known meta decks that fit the cards seen so far (a
    posterior over decks), and its hand from that deck.
  - *Rollout* (`Rollout.cs`): how a simulated game is played to the end:
    fast rules of thumb for both sides; the opponent's choices follow a
    play style fitted on recorded games.
  - *Evaluators* (`Evaluator.cs` and others): a score for a position when a
    simulation stops early, for example at a round's end.
  - *PUCT* (`Ismcts.cs`): the tree prefers plays that the policy network
    rates highly, until the simulations show otherwise.
- **Learn** (`GwentAI/Learn`): the policy network and its encoders. The
  network reads a position (cards in hand, on the board, in the graveyards)
  and gives each legal play a prior probability. It is fitted offline on
  self-play games. The card statistics it needs come from your client
  (`PolicyModel.cs`).
- **Sim** (`GwentAI/Sim`): the glue to the engine: moves as text, game
  replay, and patches that make the engine safe to copy and run many times.
- **Practice** (`GwentAI/Practice`): the Practice seat. It deals the AI a
  deck from the roster, answers each request of the game with a search,
  sends the search to the worker processes, and records each game
  (`scripts/game-report.py` lists and packs the records).

## The workers

`GwentHost --search-worker` runs searches for the game: `run-local.sh`
starts several, and each searches its own tree of the same position. The
mod merges their results (*root parallelization*), so more cores give more
simulations in the same think time. If the workers stop, the mod searches
alone.

## scripts/

`setup.sh` installs the tools, `build-local.sh` builds the mod, the host
and the helpers, `prepare-local.py` installs the mod into your client,
`run-local.sh` starts the sandbox and `local_session.py` runs the server,
the workers and the game inside it. `roster.py`, `client_decks.py`,
`card_roles.py` and `answer_kinds.py` read deck and card data from your
client. Settings: [options.md](options.md).
