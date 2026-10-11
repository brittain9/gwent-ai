"""Paths to the player's client and to this build, and the card-table constants read from the client."""

import os
from pathlib import Path
import re
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]
# The client directory (engine assemblies and assets the host loads); GWENT_CLIENT names another.
CLIENT = Path(os.environ.get('GWENT_CLIENT', ROOT.parent / 'client'))
DEFINITIONS = CLIENT / "Gwent_Data" / "StreamingAssets" / "data_definitions"
FACTIONS = {
    2: "Monsters",
    4: "Nilfgaard",
    8: "NorthernRealms",
    16: "Scoiatael",
    32: "Skellige",
}
NEUTRAL = 1
BASE_SET = 1
LEADER, BRONZE, SILVER, GOLD = 1, 2, 4, 8

TEMPLATE = re.compile(
    r'<Template Type="CardTemplate" Id="(\d+)" DebugName="([^"]*)" Availability="(\d+)">.*?'
    r"<FactionId>(\d+)</FactionId><Tier>(\d+)</Tier>"
)
# The stock AI personality a seat uses with a deck of each faction: the
# Practice opponents' (Nilfgaard and Scoia'tael have two each; these are
# the ones the deck roster uses).
STOCK_PERSONALITY = {
    'Monsters': 'p_Monsters_Practice_Easy',
    'Nilfgaard': 'p_Nilfgaard_Practice',
    'NorthernRealms': 'p_NorthernRealms_Practice',
    'Scoiatael': "p_Scoia'tael_Practice_Easy",
    'Skellige': 'p_Skellige_Practice',
}
# Mono's JIT flags for the host (see GwentHost/Program.cs for -float32);
# GWENT_MONO_FLAGS replaces them.
MONO_FLAGS = os.environ.get('GWENT_MONO_FLAGS', '-O=-float32').split()
# GWENT_HOST: another host build.
HOST = Path(os.environ.get('GWENT_HOST', ROOT / 'dist' / 'host' / 'GwentHost.exe'))
# The host on .NET (GwentHost.Core); GWENT_HOST_CORE: another build.
HOST_CORE = Path(os.environ.get('GWENT_HOST_CORE', ROOT / 'dist' / 'host-core' / 'GwentHost.Core.dll'))
LOADER = ROOT / '.local' / 'melonloader' / 'MelonLoader'
MACOS = sys.platform == 'darwin'
# The host needs x86 floating point (GwentHost/Program.cs): on macOS, the
# x86_64 Mono that scripts/setup.sh builds, under Rosetta 2. Native arm64
# Mono rounds every float to single precision and plays different games.
MONO = str(ROOT / '.local' / 'mono-x86_64' / 'bin' / 'mono') if MACOS else 'mono'


def card_pool():
    """Collectible cards and leaders of the user's client: {id: (name, faction, tier)},
    named as players see them (the English localization; the template's
    debug name when it has none)."""
    with zipfile.ZipFile(DEFINITIONS) as archive:
        text = archive.read("Templates.xml").decode("utf-8-sig")
        names = dict(re.findall(r'^"cards";"(\d+)_name";"(.*)"\r?$',
                                archive.read("Localization/en_us.csv").decode("utf-8-sig"), re.M))
    return {
        int(m[1]): (names.get(m[1], m[2]), int(m[4]), int(m[5]))
        for m in TEMPLATE.finditer(text)
        if int(m[3]) == BASE_SET
    }



def host_command(host=HOST, runtime='mono'):
    """The command line that runs a GwentHost build (GwentHost.Core on
    coreclr), before its own arguments."""
    if runtime == 'coreclr':
        return ['dotnet', str(host), '--client', str(CLIENT), '--loader', str(LOADER)]
    if MACOS and not Path(MONO).is_file():
        raise SystemExit(f'{MONO} missing; run scripts/setup.sh')
    return [MONO, *MONO_FLAGS, str(host), '--client', str(CLIENT), '--loader', str(LOADER)]
