"""The GOG service names the 2018 client contacts; each session sends them to 127.0.0.1.

Data only: the Linux and macOS sandbox's hosts file (scripts/prepare-local.py),
the Windows in-game routing (scripts/windows_paths.py) and the per-install
certificate's names (scripts/selfsigned.py) read this list.
"""

GOG_HOSTS = [
    "gwent-quests.gog.com",
    "presence.gog.com",
    "seawolf-config.gog.com",
    "seawolf-deck.gog.com",
    "seawolf-inventory.gog.com",
    "seawolf-shop.gog.com",
    "seawolf-rankings.gog.com",
    "seawolf-profile.gog.com",
    "seawolf-rewards.gog.com",
    "seawolf-matchmaking.gog.com",
    "seawolf-games-log.gog.com",
    "remote-config.gog.com",
    "notifications-pusher.gog.com",
    "users.gog.com",
    "auth.gog.com",
]
