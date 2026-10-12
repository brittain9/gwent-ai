#!/usr/bin/env python3
"""
One-shot setup for a SAME-PC (localhost / LAN) Gwent private server.

  1. Extracts card data-definitions from YOUR OWN Gwent client into the server
     data dir. Nothing CDPR-owned is shipped - it's copied from your install.
  2. Ensures a rewards.json stub exists next to the server.
  3. Creates the data dir and writes a small run config for run_local_server.py.

nginx's static responses (shop, prices, config, news) are made from the
client by scripts/server_static.py, not here.

The project's setup (scripts/setup.cmd, scripts/setup.sh --play) runs this
with --managed: it prints only what it made, because the project's session
scripts start the server.

Usage:
    python setup_local_server.py
    python setup_local_server.py "D:\\path\\to\\Gwent The Witcher Card Game"
    python setup_local_server.py --server-ip 192.168.1.50
"""

import os
import sys
import json
import argparse

HERE = os.path.dirname(os.path.abspath(__file__))


QUIET = False


def _log(msg, detail=False):
    """Prints a step; with --managed only the lines a player needs."""
    if not (QUIET and detail):
        print(("  " if QUIET else "[setup] ") + msg)


def run_extractor(client_arg, data_def_dir):
    """Extract definitions IN-PROCESS by importing the extractor.

    We deliberately do NOT shell out to `sys.executable extract_data_definitions.py`:
    inside a frozen exe sys.executable is the host exe itself, not a Python
    interpreter, so that subprocess call silently fails. Importing and calling
    the function works in both source and frozen modes.
    """
    _log("Extracting card definitions from your client...", detail=True)
    for d in (HERE, getattr(sys, "_MEIPASS", HERE)):
        if d not in sys.path:
            sys.path.insert(0, d)
    try:
        import extract_data_definitions as extractor
    except ImportError:
        sys.exit("ERROR: extract_data_definitions module not found in the bundle.")
    extractor.run_extraction(client_arg, data_def_dir, quiet=QUIET)


def ensure_rewards():
    path = os.path.join(HERE, "rewards.json")
    if os.path.exists(path):
        return
    stub = {"items": [], "total_count": 0, "limit": 2000,
            "page_token": "1", "next_page_token": None}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(stub, f, indent=4)
    _log("Wrote blank rewards.json stub.", detail=True)


def write_run_config(data_dir, server_ip):
    cfg = {
        "GWENT_DATA_DIR": data_dir,
        "GWENT_USE_SQLITE": "1",
        "GWENT_SERVER_IP": server_ip,
    }
    path = os.path.join(HERE, "local_server.cfg.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=2)
    _log("Wrote run config: " + path, detail=True)
    return cfg


def main():
    ap = argparse.ArgumentParser(description="Set up a same-PC Gwent private server.")
    ap.add_argument("client", nargs="?", help="Path to your Gwent install folder")
    ap.add_argument("--data-dir", default=os.path.join(HERE, "data"),
                    help="Where gwent.db and Data_definitions live (default: ./data)")
    ap.add_argument("--server-ip", default="127.0.0.1",
                    help="IP the server advertises. 127.0.0.1 for solo; your LAN "
                         "IP (e.g. 192.168.1.x) so a friend on your network can join.")
    ap.add_argument("--managed", action="store_true",
                    help="run by the project's setup, whose session scripts start the server: "
                         "print only what was made")
    args = ap.parse_args()
    global QUIET
    QUIET = args.managed

    data_dir = os.path.abspath(args.data_dir)
    data_def_dir = os.path.join(data_dir, "Data_definitions")
    os.makedirs(data_def_dir, exist_ok=True)
    _log("Data dir: " + data_dir, detail=True)

    run_extractor(args.client, data_def_dir)
    ensure_rewards()
    cfg = write_run_config(data_dir, args.server_ip)

    if args.managed:
        _log("server data: " + data_dir)
        return
    print()
    _log("Setup complete.")
    _log("  Server IP advertised : " + cfg["GWENT_SERVER_IP"])
    _log("  Database             : " + os.path.join(data_dir, "gwent.db"))
    _log("  Card definitions     : " + data_def_dir)
    print()
    _log("Next: start the server with  python run_local_server.py")
    if cfg["GWENT_SERVER_IP"] == "127.0.0.1":
        _log("(localhost only - re-run with --server-ip <your-LAN-IP> to let a friend join.)")


if __name__ == "__main__":
    main()
