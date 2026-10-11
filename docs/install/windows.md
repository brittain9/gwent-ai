# Windows

**Not tested on Windows yet.** These steps use upstream's Windows launcher
and the downloaded AI mod. The AI searches inside the game process on
Windows, so it is weaker than the Linux and macOS setup with helper processes.

## Install

1. Get the [client](client.md). Download and extract
   [gwent-ai-v0.1.0.zip](https://github.com/brittain9/gwent-ai/releases/download/v0.1.0/gwent-ai-v0.1.0.zip).
2. Install upstream's [Windows launcher and server](https://github.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server/releases).
   Read its [player guide](../../server/docs/PLAYER_GUIDE.md) and
   [host guide](../../server/docs/HOST_GUIDE.md). It installs MelonLoader
   0.5.7 and the restoration mod. It needs administrator rights for DNS,
   certificates and a Windows service. We have not audited its prebuilt files.
3. Run upstream's server and launcher with "I'm hosting the server on this PC".
   Check that Practice works, then close the game.
4. Install Python 3.10 or later. From the extracted `gwent-ai` folder,
   generate the card data and install the roster, replacing the client path:

   ```powershell
   python scripts/install-card-data.py --client C:\path\to\client
   python scripts/roster.py install --to C:\path\to\client\Mods
   Copy-Item dist\GwentAI.dll C:\path\to\client\Mods\GwentAI.dll
   ```

5. Start the game from upstream's launcher and choose **Single-player, then Practice**.
   `MelonLoader\Latest.log` in the client folder shows whether `GwentAI` loaded.

No .NET SDK is needed to use the downloaded DLL. Card data is generated
locally under `UserData\GwentAI\cards` in your client; rerun the generator
when updating the client or the AI. Its printed hash should match the
[release notes](../../results/v0.1.0.md).

## Return to the stock AI

Remove `Mods\GwentAI.dll`, `Mods\GwentAI-roster` and
`Mods\GwentAI-layers.json` from the client.
