# Windows

**Not tested on Windows yet.** These notes say how it should work, from how
the parts work on Linux and from upstream's documentation. Expect to fix
things. If you get it working, please open an issue and say what you did.

The client is a Windows program, so no Wine is needed. `scripts/run-local.sh`
(the Linux and macOS runner: namespaces, bubblewrap, the search workers)
does not exist on Windows.

## The plain route: upstream's launcher plus the AI mod

Upstream publishes Windows builds of its launcher and server host
([releases](https://github.com/Somnia41205/Unofficial-Gwent-Beta-0.9.24.3.432-Private-Server/releases)).
They install MelonLoader 0.5.7 and the restoration mod into the client, run
the server on your PC, and redirect the GOG hostnames. They need
administrator rights: they change DNS settings, install a certificate and
register a Windows service. Read upstream's
[PLAYER_GUIDE](../../server/docs/PLAYER_GUIDE.md) and
[HOST_GUIDE](../../server/docs/HOST_GUIDE.md) first.

Note: these releases contain prebuilt binaries (`nginx.exe`,
`GalaxyCommunication.exe`, the restoration DLL) that the project's copy of
the server does not carry; the project builds them from source on Linux and
macOS. We have not audited upstream's builds.

Prerequisites: the .NET SDK, Mono 6 for Windows (mono-project.com), Python
3.10 or newer, uv.

1. Extract the client ([client.md](client.md)).
2. Start upstream's server host on this PC, then the launcher with "I'm
   hosting the server on this PC". Check that Practice works with the stock
   AI.
3. Close the game. Build the AI mod against the client and the MelonLoader
   the launcher installed. `scripts/build-local.sh` does this: it derives the
   card tables from your client (Python), then runs `dotnet build`. It is a
   shell script and has not been run on Windows; from a Unix-style shell
   (Git Bash or WSL) with the project beside the client, it should work as on
   Linux. The `dotnet build` step by hand, with the paths of your client and
   its MelonLoader folder:

   ```powershell
   dotnet build -c Release --ignore-failed-sources -p:ClientDir=C:\path\to\client `
       -p:LoaderDir=C:\path\to\client\MelonLoader GwentAI\GwentAI.csproj
   ```

   The build prints a `card table: sha256 ...` line. The value should match
   the one in the release notes (the `results/` file for your release).

4. Copy the build into the client's `Mods` folder: `dist\GwentAI.dll`, and
   the deck roster, which `python scripts\roster.py install --to
   C:\path\to\client\Mods` writes there as the folder `GwentAI-roster` and
   the file `GwentAI-layers.json`. The mod loads the roster from the folder
   it sits in.
5. Start the game from the launcher and choose **Single-player**, then
   **Practice**. `MelonLoader\Latest.log` in the client folder shows whether
   `GwentAI` loaded.

To go back to the stock AI, delete `Mods\GwentAI.dll`, `Mods\GwentAI-roster`
and `Mods\GwentAI-layers.json`.

## Strength on Windows

On Linux and macOS, `scripts/run-local.sh` starts the search workers: helper
processes that run the AI's search on several cores. Nothing starts them on
Windows, so there the AI runs its search inside the game process until
someone writes the Windows runner. It plays the same way but weaker: fewer
imagined games fit the same thinking time.
