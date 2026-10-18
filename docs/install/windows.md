# Windows

Tested on Windows 11 (x86-64) for release 0.2.0. The game is a Windows
program, so Windows needs no Wine and no sandbox, and it plays the same AI
with the same search workers as Linux and macOS.

## What you need

- The [client](client.md), extracted to a folder named `client`.
- Python 3.10 to 3.14 (64-bit), and the .NET 10 runtime for the AI's search workers.
  In PowerShell:

  ```powershell
  winget install Python.Python.3.12
  winget install Microsoft.DotNet.Runtime.10
  ```

  Open a new window afterwards so `python` and `dotnet` are found.

## Install

1. [Download the latest release](https://github.com/brittain9/gwent-ai/releases/latest)
   and extract it next to the `client` folder:

   ```text
   Games\Gwent\
   ├── client\     Gwent.exe, Gwent_Data\, ...
   └── gwent-ai\   this project
   ```

2. Open PowerShell or Command Prompt in `gwent-ai` and run, once:

   ```powershell
   scripts\setup.cmd
   ```

   If your client is somewhere else: `scripts\setup.cmd --client D:\path\to\client`.
   Setup needs no administrator rights. It downloads what it needs, checks
   each file's SHA-256, reads the card data from your client and installs the
   AI into it. It prints `card table: sha256 db0dbb0424c3e29b`; the same value
   means your client matches the one the AI was measured with.

## Play

```powershell
scripts\run-local.cmd
```

The game starts and signs you in to a server on your own PC. Choose
**Single-player**, then **Practice**. Close the game to end the session; the
server and the AI's helper processes stop with it.

The AI thinks for about 10 seconds on a clear move and up to 30 on a hard
one. It uses the time, not a fixed amount of search, so a faster processor
with more cores lets it look further ahead in the same time.

Options: `--windowed`, `--workers N` (helper processes; `0` searches inside
the game, which is weaker), `--stock-ai` (the game's own AI). See
[options.md](options.md). `scripts\run-local.cmd --smoke` checks the server
and a search worker without starting the game.

## Emotes and arena

Both are on by default. They were tested on Linux and macOS, not yet on
Windows.

- **Emotes.** The opponent uses the game's taunts like a person: some games
  none, some games a few. To turn them off, set `GWENT_AI_EMOTES=0` before
  you start the session.
- **Arena, an early version.** The game's own arena screens work against
  the AI: a free ticket, a draft of 26 cards, a leader, and a run that ends
  at 9 wins or 3 losses. The AI drafts its deck at random, so it is easier
  to beat in arena than in Practice. To turn it off, set `GWENT_AI_ARENA=0`
  before you start the session. You get one new ticket each time the game
  starts, so restart the client for a new run.

The session reads them from the environment of the shell that runs
`scripts\run-local.cmd`. In `cmd`, run `set GWENT_AI_EMOTES=0`; in
PowerShell, run `$env:GWENT_AI_EMOTES = "0"`. Do this before you run it.

## What it changes on your PC

Nothing outside two folders, and no administrator rights:

- **The `gwent-ai` folder:** the downloads (MelonLoader 0.5.7, nginx from
  nginx.org, the search workers' libraries from nuget.org, each checked by
  SHA-256), a Python environment for the server, and the server's data, logs
  and this install's certificate in `.runtime`.
- **The `client` folder:** MelonLoader, the restoration mod and the AI in
  `Mods\`, the `GwentSessionNet` plugin in `Plugins\`, the AI's card data in
  `UserData\`, and two settings files in `Gwent_Data\StreamingAssets\Settings\`
  (setup keeps copies of the originals).

The game expects GOG's servers, which are gone. Setup makes a certificate for
the local server on your PC, with its own private key that never leaves the
`gwent-ai` folder. While the game runs, the `GwentSessionNet` plugin sends
the game's GOG connections to the server on 127.0.0.1 and lets the game
accept that one certificate, inside the game only. Setup does not edit the
hosts file, change DNS, network or firewall settings, install a Windows
service, or add anything to Windows' trusted certificates.

The first time the game starts, Windows Firewall may ask whether Gwent.exe
may use public and private networks. Choose **Cancel**: the game only talks
to the server on your own PC.

If the game still cannot sign in, a fallback is
`scripts\setup.cmd --trust-cert`: it adds this install's certificate to your
Windows user's trusted certificates (Windows asks you to confirm). It is
valid only for the game's GOG server names, it cannot vouch for any other
certificate, and its key stays on your PC.
`scripts\setup.cmd --untrust-cert` removes it.

## Uninstall

```powershell
scripts\setup.cmd --uninstall
```

removes what setup added to the client (MelonLoader too, unless the client
had it before), puts back the client's own settings files, and removes the
trusted certificate if you added one. Then delete the `gwent-ai` folder.

## If something goes wrong

- **Setup says .NET 10 is missing:** run the `winget` line above, then open
  a new window.
- **"Failed building wheel for lz4":** your Python has no prebuilt lz4
  wheel. Install Python 3.12 with the `winget` line above, open a new
  window and run setup again.
- **"Ports in use":** another program holds a port the server needs. GOG
  Galaxy holds 9977, so quit it; a local web server or VPN client may hold
  443.
- **The game cannot sign in:** see the fallback above.
- **The AI does not move, or the stock AI plays:** `client\MelonLoader\Latest.log`
  shows whether `GwentAI` and `GwentSessionNet` loaded.
- **Session logs** are in `gwent-ai\.runtime\logs`. Include them in an issue.
