# The local server

The game client of the 2018 public beta needs its online services to start.
This folder is the server that answers them on your own PC, on 127.0.0.1
only: a Python backend that replaces the online services, the nginx routing,
the Galaxy service stand-in (`comet-main`) and the client restoration mod
(`GwentBetaRestorationMod`). The project's setup and session scripts run it;
you never start it by hand.

It began as Somnia41205's Unofficial Gwent Beta private server (MIT), whose
work made the 2018 client playable again. Thank you. [UPSTREAM.md](UPSTREAM.md)
says which version it came from and what changed here; [LICENSE](LICENSE) is
Somnia41205's MIT licence, and [docs/NOTICE.md](docs/NOTICE.md) lists the
third-party parts. [docs/FEATURES.md](docs/FEATURES.md) lists what the server
supports.
