#!/usr/bin/env python3
"""Derive the AI's card data from your client and install it for that client."""

import argparse
import hashlib
import json
from pathlib import Path

import answer_kinds
import card_roles
import clientdata


def write_card_data(directory, data, params, engine, definitions):
    """Write a matching card-table pair and identify the engine it describes."""
    text = answer_kinds.roles_text(data)
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    engine_digest = hashlib.sha256(Path(engine).read_bytes()).hexdigest()
    definitions_digest = hashlib.sha256(Path(definitions).read_bytes()).hexdigest()
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    files = {
        "roles.json": text,
        "answer-params.json": json.dumps({**params, "roles_sha256": digest}, indent=1) + "\n",
        "manifest.json": json.dumps({"engine_sha256": engine_digest, "definitions_sha256": definitions_digest}, indent=1) + "\n",
    }
    for name, content in files.items():
        temporary = directory / (name + ".part")
        temporary.write_text(content, encoding="utf-8")
        temporary.replace(directory / name)
    return digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path, default=clientdata.CLIENT)
    parser.add_argument("--out", type=Path, help="output directory; default: the client's UserData/GwentAI/cards")
    args = parser.parse_args()
    definitions = args.client / "Gwent_Data/StreamingAssets/data_definitions"
    engine = args.client / "Gwent_Data/Managed/Assembly-CSharp.dll"
    if not definitions.is_file() or not engine.is_file():
        parser.error("client must contain Gwent_Data/StreamingAssets/data_definitions and Gwent_Data/Managed/Assembly-CSharp.dll")
    data, params = answer_kinds.split(card_roles.build(card_roles.load_client(definitions)))
    output = args.out or args.client / "UserData/GwentAI/cards"
    digest = write_card_data(output, data, params, engine, definitions)
    print(f"card table: sha256 {digest[:16]}")
    print(f"Card data installed in {output}")


if __name__ == "__main__":
    main()
