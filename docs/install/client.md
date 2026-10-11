# The Gwent client

The project needs the original **Gwent public beta client, version
0.9.24.3.432** (June 2018). The AI runs inside it, and setup reads the
card tables from it.

The client is CD PROJEKT RED's software. It is **not included** in the
project, and nothing here redistributes its files. The project is not
affiliated with or endorsed by CD PROJEKT RED.

## Your copy

The project does not host or link the client. Use your own copy of this
build:

| | |
| --- | --- |
| File | `Gwent 0.9.24.3.432.zip` |
| Size | 2,428,834,206 bytes |
| MD5 | `39b3782dcb592ac4012723a19bdc5b68` |
| SHA-256 | `145cb314466fae0c8db0347875d2468f20d8281c3cee0fcdbe32758aee1235ff` |

These are the values of the copy the project was developed with. Check
yours before you use it:

```sh
sha256sum "Gwent 0.9.24.3.432.zip"
```

## Provenance

- GOG published this build on 2018-06-08. Its metadata is on GOGDB:
  [product 1971477531, build 51286822954628908](https://www.gogdb.org/product/1971477531/build/51286822954628908).
  GOGDB lists builds; it is not a download source.
- `Gwent.exe` SHA-256:
  `bacc0d9774640213f698ae0400023041f8df5aba753ea595fa3b449206198231`.
  `scripts/setup.sh` checks this hash and warns when it differs.
- Engine: Unity 2017.3.1p2. At run time the client reports
  `0.9.24.3_None@websockets`.

A matching hash shows that your copy is the same as ours. It does not prove
that the archive is authentic: no CDPR-published manifest exists to compare
against.

## Where to put it

Extract the archive into a directory named `client` **next to** the
project. The archive has no top-level folder.

```sh
cd ~/games/gwent                     # any parent directory
unzip "Gwent 0.9.24.3.432.zip" -d client
git clone https://github.com/brittain9/gwent-ai.git
```

```text
~/games/gwent/
├── client/        Gwent.exe, Gwent_Data/, ...
└── gwent-ai/      the project
```

Every script finds the client at `../client`. The C# builds accept another
location with `-p:ClientDir=/path/to/client`.

## What the scripts add to the client

The scripts **only add files** to the client; they change none of the
original files. Checked on our copy: all 926 original files were
byte-identical after setup.

| Added | What it is |
| --- | --- |
| `version.dll`, `MelonLoader/`, `NOTICE.txt` | MelonLoader 0.5.7, the mod loader |
| `UserData/` | Made by MelonLoader at first start |
| `Mods/GwentBetaRestorationMod.dll` | The community server's restoration mod, built from source |
| `Mods/GwentAI.dll`, `Mods/GwentAI-roster/`, `Mods/GwentAI-layers.json` | The AI and its deck roster |
| `Gwent_Data/StreamingAssets/Settings/config.json`, `Launch.cfg` | Point the client at the local server |

Delete these to get the stock client back.
