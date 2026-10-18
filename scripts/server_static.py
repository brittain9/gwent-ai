#!/usr/bin/env python3
"""The static responses nginx serves the client (server/host_nginx.conf
`root html`), made from your own client:

    python scripts/server_static.py [--client DIR] [--out DIR]   # default .runtime/nginx-html

    game/<version>_<secret>/config.json             the remote configuration
    game/<version>_<secret>/news.json               no news
    game/<version>_<secret>/news/1/GOG/news.initial.en-us
    shop/platforms/GOG/products                     one card keg, one free arena ticket
    card_definitions/prices                         card prices (see below)

The client asks for its configuration under its game version and data secret,
which it holds in the Settings asset of Gwent_Data/resources.assets; they are
read from there, so nothing derived from the game is kept in this project. The
response bodies are those of Somnia41205's server (MIT, server/LICENSE),
byte for byte, except the shop's free arena ticket (server/docs/arena.md).

card_definitions/prices (crafting, milling and transmutation costs per
rarity) is the one response the client cannot supply. It is copied from
server/Nginx/html when that file is present; otherwise
the response holds no prices, {"base_prices": [], "price_overrides": []}.
The client asks for it during login (state 1007, InitializeCardPrices) and
sends the player back to the start of login when the request fails; it
reads both lists without a null check
(CardDefinitionsHolder.AddPricesToDictionary), so neither may be missing.
"""

import argparse
from pathlib import Path
import re
import shutil

import clientdata

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / ".runtime" / "nginx-html"
PRICES = ROOT / "server" / "Nginx" / "html" / "card_definitions" / "prices"
# Without the prices file: a valid response that holds no prices (see above).
NO_PRICES = '{"base_prices": [], "price_overrides": []}\n'

SETTINGS = re.compile(rb"<Settings>\s*<GameVersion>([0-9.]+)</GameVersion>\s*<LogicVersion>\d+</LogicVersion>"
                      rb"\s*<Secret>([A-Za-z0-9]+)</Secret>")

CONFIG = """{
  "messages_hash": "",
  "configs": [
    {
      "conditions": {
        "from": "2017-01-01T00:00:00",
        "to": "2030-01-01T00:00:00",
        "game_versions": ["14"],
        "is_final": false
      },
      "features": {
        "login": true,
        "play_ranked": true,
        "invites": true,
        "shop": true,
        "open_keg": true,
        "gameplay_tutorial": false,
        "shop_tutorial": false,
        "deckbuilder_tutorial": false,
        "booth_version": false,
        "patch_notes": false,
        "singleplayer_campaign": true,
\t\t"network": "",
        "skip_license_check": false,
\t\t"menu_sections": [],
\t\t"submenu_sections": []
      },
      "data": {
        "version": "14",
        "hash": "SECRET",
        "patchnotes_version": "14"
      },
      "messages": [],
      "infobar": [],
      "debug": {}
    }
  ]
}"""

NEWS = """{
  "version": "1",
  "news_list": [],
  "social_media_links_list": []
}"""

PRODUCTS = ('{"items":[{"id":"49085943257433578","platform_product_id":null,"platform_specific_id":null,'
            '"platform":"GOG","consumable":true,"country":null,"prices":[{"currency":"VIRTUAL","price":100,'
            '"display_price":"100","available_from":"2010-01-01T01:00:00+0000",'
            '"available_to":"2077-01-01T01:00:00+0000","type":"normal"}],"image_url":"","image_hash":null,'
            '"translated_product_info":{"language":"en-US","name":"Gwent Card Keg",'
            '"description":"A keg containing 5 cards.","category_name":"Card Packs"},'
            '"items":[{"type":"inventory_item","item_id":"1","count":1}],"category":{"id":"CardPacks"}},'
            '{"id":"8000000000000001","platform_product_id":null,"platform_specific_id":null,'
            '"platform":"GOG","consumable":true,"country":null,"prices":[{"currency":"VIRTUAL","price":0,'
            '"display_price":"0","available_from":"2010-01-01T01:00:00+0000",'
            '"available_to":"2077-01-01T01:00:00+0000","type":"normal"}],"image_url":"","image_hash":null,'
            '"translated_product_info":{"language":"en-US","name":"Arena Ticket",'
            '"description":"One arena run, free on this server.","category_name":"Arena"},'
            '"items":[{"type":"inventory_item","item_id":"8000","count":1}],"category":{"id":"ArenaTickets"}}],'
            '"total_count":2,"limit":"9223372036854775807","page_token":1,"next_page_token":null}')


def client_settings(client):
    """(game version, data secret) from the client's Settings asset."""
    path = Path(client) / "Gwent_Data" / "resources.assets"
    match = SETTINGS.search(path.read_bytes())
    if not match:
        raise SystemExit(f"{path}: no Settings asset with a GameVersion and Secret; is this Gwent 0.9.24.3.432?")
    return match[1].decode("ascii"), match[2].decode("ascii")


def responses(client):
    """{relative path: body} for the client."""
    version, secret = client_settings(client)
    game = f"game/{version}_{secret}"
    return {
        f"{game}/config.json": CONFIG.replace("SECRET", secret),
        f"{game}/news.json": NEWS,
        f"{game}/news/1/GOG/news.initial.en-us": NEWS,
        "shop/platforms/GOG/products": PRODUCTS,
    }


def write(client, out=OUT, prices=PRICES):
    """Writes the responses below out (LF, UTF-8); returns the paths written."""
    out = Path(out)
    written = []
    for relative, body in responses(client).items():
        path = out / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        if not path.is_file() or path.read_bytes() != body.encode("utf-8"):
            path.write_bytes(body.encode("utf-8"))
        written.append(path)
    target = out / "card_definitions" / "prices"
    target.parent.mkdir(parents=True, exist_ok=True)
    body = (Path(prices).read_bytes().replace(b"\r\n", b"\n") if Path(prices).is_file()
            else NO_PRICES.encode("utf-8"))
    if not target.is_file() or target.read_bytes() != body:
        target.write_bytes(body)
    written.append(target)
    return written


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--client", type=Path, default=clientdata.CLIENT)
    parser.add_argument("--out", type=Path, default=OUT)
    args = parser.parse_args()
    written = write(args.client, args.out)
    print(f"{len(written)} static responses in {args.out}")


if __name__ == "__main__":
    main()
