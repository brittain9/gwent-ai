#!/usr/bin/env python3
"""List your recorded Practice games and pack one or more for a game report.

Every Practice game against the AI is recorded as one JSON file
(data/human-games). A record holds exactly:

- both decks as card ids (faction, leader, cards) and the name of the AI's deck;
  the name of your deck is in your copy only and is left out of a pack;
- the game's seed, the round-1 starter and every move with its round;
- the round scores, the round winners, the winner and how the game ended;
- the AI's search statistics and timings for each of its decisions, and the
  attacks on units in the game;
- the AI's build id and search settings, and the number of search workers;
- the start time in UTC (the file name is the same time and the seed).

No account or login name is in it. A decision the AI searched in its own
process when its search workers were unavailable names the reason in
`ai_decisions[].Source`, and that reason can hold a file path: a pack keeps
only the text before the first ':' (for example "local"). To report a game,
attach the pack this script writes to an issue titled "Game report" and say
what you would have played.

  scripts/game-report.py            the last 10 games, newest first
  scripts/game-report.py --pack     pack the newest game into game-report-<date>.zip
  scripts/game-report.py --pack 3   pack the newest 3 games
  scripts/game-report.py --pack FILE ...   pack these record files
"""

import argparse
import json
import sys
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RECORD_DIRS = [ROOT / "data" / "human-games", ROOT / ".runtime" / "human-games"]


def records():
    found = {}
    for directory in RECORD_DIRS:
        for path in directory.glob("*.json"):
            found.setdefault(path.name, path)
    return sorted(found.values(), key=lambda p: p.name, reverse=True)


def summary(path):
    game = json.loads(path.read_text(encoding="utf-8"))
    human, ai = game.get("human", "P1"), game.get("ai", "P2")
    decks = {seat: game.get(f"{seat.lower()}_deck") or {} for seat in ("P1", "P2")}
    scores = " ".join(f"{a}-{b}" for a, b in game.get("round_scores", []))
    winner = game.get("winner")
    result = (
        "you won" if winner == human else "the AI won" if winner == ai else "no result"
    )
    return (
        f"{path.name}  {game.get('started', '')[:16]} UTC  you: {decks[human].get('faction')}"
        f"  AI: {decks[ai].get('faction')}  rounds {scores}  {result}"
    )


def packed(path):
    """The record as it goes into a pack: without the name of the player's deck
    (a name the player typed), and with each decision's Source cut to the text
    before the first ':' (the rest can be an error message with a path)."""
    game = json.loads(path.read_text(encoding="utf-8"))
    deck = game.get(f"{game.get('human', 'P1').lower()}_deck")
    if isinstance(deck, dict):
        deck.pop("name", None)
    for decision in game.get("ai_decisions") or []:
        if isinstance(decision, dict) and isinstance(decision.get("Source"), str):
            decision["Source"] = decision["Source"].split(":", 1)[0]
    return game


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument(
        "--pack",
        nargs="*",
        metavar="N|FILE",
        help="pack the newest N games (default 1) or the given files into a zip",
    )
    parser.add_argument(
        "--list", type=int, default=10, metavar="N", help="how many games to list"
    )
    args = parser.parse_args(argv)

    found = records()
    if not found:
        print("no recorded games yet (records are written while you play Practice)")
        return 1
    if args.pack is None:
        for path in found[: args.list]:
            print(summary(path))
        print(
            f"\n{len(found)} games recorded. Pack one for a report: scripts/game-report.py --pack"
        )
        return 0

    if not args.pack:
        chosen = found[:1]
    elif len(args.pack) == 1 and args.pack[0].isdigit():
        chosen = found[: int(args.pack[0])]
    else:
        chosen = [Path(name) for name in args.pack]
        missing = [str(p) for p in chosen if not p.is_file()]
        if missing:
            print("not found: " + ", ".join(missing), file=sys.stderr)
            return 2
    out = ROOT / f"game-report-{time.strftime('%Y%m%d-%H%M%S')}.zip"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as pack:
        for path in chosen:
            pack.writestr(path.name, json.dumps(packed(path), indent=2))
            print(summary(path))
    print(
        f'\nwrote {out.relative_to(ROOT)} ({len(chosen)} games). Attach it to an issue titled "Game report".'
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
