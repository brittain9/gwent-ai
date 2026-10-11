#!/usr/bin/env python3
"""The deck roster the Practice AI plays against and reasons over.

    scripts/roster.py import-client             # Practice and low-level decks from your client
    scripts/roster.py install [--to DIR]        # copy the roster beside the client mod

Community decks live in data/decks/roster (with credit); decks derived from your
client stay in .runtime/decks/roster, and the client's seven Practice decks in
.runtime/decks/practice. `install` copies the roster and the layers file
(data/decks/layers.json) next to the mod (client/Mods/GwentAI-roster,
GwentAI-layers.json), where Practice reads them.
"""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import shutil
import subprocess

from clientdata import BRONZE, CLIENT, FACTIONS, ROOT, STOCK_PERSONALITY, card_pool, host_command

ROSTER = ROOT / "data" / "decks" / "roster"
LOCAL_ROSTER = ROOT / ".runtime" / "decks" / "roster"
LAYERS = ROOT / "data" / "decks" / "layers.json"
SOURCES = ROOT / ".runtime" / "decks" / "sources"

# Client decks for the low-level layer: what the client handed beginners
# (faction-challenge AI decks, about 2 gold / 3 silver / 20 bronze, and the
# loaner decks given to the player for each challenge) and the Practice
# opponents. Tutorial decks are scripted fragments and left out.
CLIENT_DECK = re.compile(r"^(ch_fac_[a-z]+_\d+(_player)?|d_.*Practice.*)$")


# The client's seven Practice battles: deck asset -> (file, battle, the
# personality its top player uses). The personality is not derivable from the
# asset names.
PRACTICE = {
    "d_Monsters_Practice_Easy": ("monsters", "Monsters_Practice", "p_Monsters_Practice_Easy"),
    "d_Nilfgaard_Practice": ("nilfgaard", "Nilfgaard_Practice", "p_Nilfgaard_Practice"),
    "d_Nilfgaard_Practice_Conceal": ("nilfgaard-conceal", "Nilfgaard_Practice_Conceal", "p_Nilfgaard_Practice_Conceal"),
    "d_NorthernRealms_Practice": ("northernrealms", "NorthernRealms_Practice", "p_NorthernRealms_Practice"),
    "d_Scoia'tael_Practice_Easy": ("scoiatael", "Scoia'tael_Practice", "p_Scoia'tael_Practice_Easy"),
    "d_Scoiatael_Practice_Elf": ("scoiatael-elf", "Scoia'tael_Practice_Elf", "p_Scoia'tael_Practice_Elf"),
    "d_Skellige_Practice_Easy": ("skellige", "Skellige_Practice", "p_Skellige_Practice"),
}
PRACTICE_DECKS = ROOT / ".runtime" / "decks" / "practice"


def slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def personalities():
    """Each faction's Practice personality: stock seats need one."""
    return STOCK_PERSONALITY


def roster_decks():
    for directory in (ROSTER, LOCAL_ROSTER):
        for path in sorted(directory.glob("*.json")):
            yield path, json.loads(path.read_text())


def write_practice(decks, pool):
    """The Practice opponents as the client holds them (entries in asset
    order, split entries kept), for games against the stock decks."""
    found = {deck["asset"]: deck for deck in decks if deck["asset"] in PRACTICE}
    missing = sorted(set(PRACTICE) - set(found))
    if missing:
        raise SystemExit(f"Practice decks missing from the client's assets: {missing}")
    PRACTICE_DECKS.mkdir(parents=True, exist_ok=True)
    for asset, (stem, battle, personality) in PRACTICE.items():
        deck = found[asset]
        leader = pool[deck["leader"]]
        spec = {"name": f"Practice: {leader[0]}", "faction": FACTIONS[leader[1]],
                "leader": {"id": deck["leader"], "name": leader[0]},
                "cards": [{"id": card["id"], "name": pool[card["id"]][0], "count": card["count"]}
                          for card in deck["cards"]],
                "personality": personality, "source": f"Installed client Practice battle {battle}"}
        (PRACTICE_DECKS / f"{stem}.json").write_text(json.dumps(spec, indent=2, ensure_ascii=False) + "\n")
    print(f"{len(PRACTICE)} Practice decks -> {PRACTICE_DECKS}")


def drop_invalid(paths):
    """Deletes the deck files the engine's validator rejects; returns why."""
    rejected = []
    if not paths:
        return rejected
    result = subprocess.run([*host_command(), "--validate-decks", *map(str, paths)], capture_output=True, text=True)
    for line in result.stdout.splitlines():
        if line.startswith("INVALID"):
            path = Path(line.split("\t")[-1])
            rejected.append(f"{path.name}: {line.split(chr(9))[1]}")
            path.unlink()
    return rejected


def import_client(args=None):
    """The client's decks (scripts/client_decks.py output): its seven Practice
    decks, and the low-level layer of the local roster. A roster deck over a
    copy limit is cut to the limit (the only unambiguous repair); decks the
    engine still rejects are dropped."""
    source = SOURCES / "client-decks.json"
    if not source.exists():
        raise SystemExit(f"{source} missing; run scripts/setup.sh (it runs scripts/client_decks.py, then this)")
    pool = card_pool()
    write_practice(json.loads(source.read_text()), pool)
    personality = personalities()
    LOCAL_ROSTER.mkdir(parents=True, exist_ok=True)
    for old in LOCAL_ROSTER.glob("client-*.json"):
        old.unlink()
    written, repaired = [], []
    for deck in json.loads(source.read_text()):
        if not CLIENT_DECK.match(deck["asset"]):
            continue
        unowned = [card["id"] for card in deck["cards"] + [{"id": deck["leader"]}] if card["id"] not in pool]
        if unowned:
            # Challenge decks may hold cards no player could own.
            print(f'dropped {deck["asset"]}: cards outside the collectible set {unowned}')
            continue
        leader = pool[deck["leader"]]
        faction = FACTIONS[leader[1]]
        # The assets sometimes split one card's copies over several entries.
        merged = Counter()
        for card in deck["cards"]:
            merged[card["id"]] += card["count"]
        cards = []
        for card in ({"id": card_id, "count": count} for card_id, count in merged.items()):
            name, _, tier = pool[card["id"]]
            limit = 3 if tier == BRONZE else 1
            if card["count"] > limit:
                repaired.append(f'{deck["asset"]}: {name} x{card["count"]} -> x{limit}')
            cards.append({"id": card["id"], "name": name, "count": min(card["count"], limit)})
        role = "practice opponent" if "Practice" in deck["asset"] else "challenge loaner" if deck["asset"].endswith("_player") else "challenge AI"
        spec = {"name": f'Client {role}: {leader[0]} ({deck["asset"]})', "faction": faction,
                "leader": {"id": deck["leader"], "name": leader[0]}, "cards": cards,
                "personality": personality[faction], "kind": "low", "tier": None, "weight": 1.0,
                "source": f'client asset {deck["asset"]}', "credit": "CD PROJEKT RED (the user's own client)"}
        path = LOCAL_ROSTER / f'client-{slug(deck["asset"])}.json'
        path.write_text(json.dumps(spec, indent=1) + "\n")
        written.append(path)
    rejected = drop_invalid(written)
    for line in repaired:
        print("repaired " + line)
    for line in rejected:
        print("dropped " + line)
    print(f"{len(written) - len(rejected)} client decks kept, {len(rejected)} dropped, {len(repaired)} copy repairs")


def install(to, layers=None):
    """Copies the roster beside the client mod (`to`/GwentAI-roster, layers in
    `to`/GwentAI-layers.json), where Practice reads it. `layers` is the layers
    dict to write; without it the layers file is copied as it is. Returns the
    number of decks."""
    target = to / "GwentAI-roster"
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True)
    count = 0
    for path, _ in roster_decks():
        shutil.copy2(path, target / path.name)
        count += 1
    if layers is None:
        shutil.copy2(LAYERS, to / "GwentAI-layers.json")
    else:
        (to / "GwentAI-layers.json").write_text(json.dumps(layers, indent=1, ensure_ascii=False) + "\n")
    return count


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("import-client").set_defaults(func=import_client)
    install_parser = sub.add_parser("install")
    install_parser.add_argument("--to", type=Path, default=CLIENT / "Mods",
                                help="the folder holding GwentAI.dll (default: the client's Mods)")
    install_parser.set_defaults(func=lambda args: print(
        f"{install(args.to)} decks -> {args.to / 'GwentAI-roster'}"))
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
