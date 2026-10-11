#!/usr/bin/env python3
"""Extract the deck definitions shipped in the user's own client (local use only).

    uv run --no-project --with UnityPy==1.25.3 --with TypeTreeGeneratorAPI==0.0.10 python scripts/client_decks.py
        [--out .runtime/decks/sources/client-decks.json]

The client keeps its scripted decks (Practice opponents, faction challenges
and their loaner decks, tutorials) as Unity DeckDefinition assets in
Gwent_Data/*.assets. This reads them with UnityPy and writes
[{asset, leader, cards: [{id, count}]}]. The output is CDPR data: it stays
under .runtime on the user's machine.

UnityPy's type tree generator misses the alignment after MonoBehaviour's
m_Enabled byte for this Unity version (2017.3.1p2); the generated tree is
corrected before reading.
"""

import argparse
import json
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers.TypeTreeNode import TypeTreeNode

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT.parent / "client" / "Gwent_Data"
DECK_TYPE = "GameplayVisuals.Battle.DeckDefinition"


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--out",
        type=Path,
        default=ROOT / ".runtime" / "decks" / "sources" / "client-decks.json",
    )
    args = parser.parse_args()
    files = [
        p
        for p in DATA.iterdir()
        if p.is_file() and (p.suffix == ".assets" or p.name.startswith("level"))
    ]
    generator = TypeTreeGenerator("2017.3.1p2")
    generator.load_local_dll_folder(str(DATA / "Managed"))
    env = UnityPy.load(*map(str, files))
    env.typetree_generator = generator
    decks = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        head = obj.parse_monobehaviour_head()
        if head.m_Script.m_PathID == 0:
            continue
        script = head.m_Script.deref_parse_as_object()
        if f"{script.m_Namespace}.{script.m_ClassName}" != DECK_TYPE:
            continue
        nodes = generator.get_nodes(script.m_AssemblyName, DECK_TYPE)
        fixed = TypeTreeNode.from_list(
            [
                TypeTreeNode(
                    n.m_Level,
                    n.m_Type,
                    n.m_Name,
                    0,
                    0,
                    m_MetaFlag=n.m_MetaFlag
                    | (0x4000 if n.m_Name == "m_Enabled" and n.m_Level == 1 else 0),
                )
                for n in nodes
            ]
        )
        data = obj.read_typetree(nodes=fixed)
        decks.append(
            {
                "asset": data["m_Name"],
                "leader": data["Leader"]["TemplateId"],
                "cards": [
                    {"id": c["Definition"]["TemplateId"], "count": c["Quanity"]}
                    for c in data["Cards"]
                ],
            }
        )
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(
        json.dumps(sorted(decks, key=lambda d: d["asset"]), indent=1) + "\n"
    )
    print(f"{len(decks)} decks -> {args.out}")


if __name__ == "__main__":
    main()
