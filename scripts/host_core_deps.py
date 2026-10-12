#!/usr/bin/env python3
"""Complete a downloaded dist/host-core (the search workers on .NET) with its
NuGet packages.

    python scripts/host_core_deps.py [DIR]          # default dist/host-core
    python scripts/host_core_deps.py --pin DIR      # rewrite the pins from a local build

The release carries only our own files (GwentHost.Core.dll, GwentAI.dll and the
deps.json and runtimeconfig.json); the packages the host uses (HarmonyX,
MonoMod, Mono.Cecil, System.Security.Permissions, ...) come from nuget.org.
GwentHost.Core.deps.json names each package and version and its SHA-512; the
package is fetched, read only when it is a plain zip (no zip64, no duplicate
names, the central directory ending at the end record, every entry where
Python's zipfile finds it), checked against that SHA-512, and the runtime
files it lists are extracted beside the host. Each extracted file must also
have the SHA-256 pinned in scripts/host-core-packages.json, so a package that
passes the first checks still cannot place a file this project did not build
and test with. Packages land in .local/downloads/nuget.

--pin writes host-core-packages.json from a local build (DIR, after
`dotnet build GwentHost.Core`): each file's hash is read from the NuGet
global packages folder, whose package must match the deps.json's SHA-512, and
must equal the file the build placed in DIR.
"""

import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import struct
import sys
import zipfile


ROOT = Path(__file__).resolve().parents[1]
HOST_CORE = ROOT / "dist" / "host-core"
DOWNLOADS = ROOT / ".local" / "downloads" / "nuget"
MANIFEST = Path(__file__).resolve().with_name("host-core-packages.json")
FEED = "https://api.nuget.org/v3-flatcontainer"


def packages(deps):
    """[(id, version, sha512 base64, {file in package: path under the host})]."""
    target = next(iter(deps["targets"].values()))
    result = []
    for key, library in deps["libraries"].items():
        if library.get("type") != "package":
            continue
        name, version = key.split("/", 1)
        sha512 = library["sha512"]
        if not sha512.startswith("sha512-"):
            raise SystemExit(f"{key}: no SHA-512 in the deps.json")
        entry = target.get(key, {})
        files = {}
        for file in entry.get("runtime", {}):
            files[file] = PurePosixPath(file).name                # lib/<tfm>/X.dll -> X.dll
        for file, details in entry.get("runtimeTargets", {}).items():
            if details.get("assetType") == "runtime":
                files[file] = file                                # runtimes/<rid>/... keeps its path
        result.append((name, version, sha512[len("sha512-"):], files))
    return result


SIGNATURE = ".signature.p7s"
END = b"PK\x05\x06"
ZIP64_LOCATOR = b"PK\x06\x07"


def archive_records(data):
    """The central directory records of a plain zip, after the checks that
    make every reader see the same entries: one end record at the end of the
    data, no zip64, one disk, the central directory ending exactly at the end
    record, unique names, each local header where its record says, and
    Python's zipfile reading the same entries at the same offsets. Raises
    ValueError otherwise. Returns (records, end offset)."""
    end = data.rfind(END)
    if end < 0 or len(data) - end < 22:
        raise ValueError("not a zip archive")
    disk, directory_disk, entries_disk, entries, directory_size, directory_offset, comment_length = \
        struct.unpack_from("<HHHHIIH", data, end + 4)
    if end + 22 + comment_length != len(data):
        raise ValueError("the end record is not at the end of the archive")
    if (data[max(0, end - 20):end - 16] == ZIP64_LOCATOR
            or 0xFFFF in (disk, directory_disk, entries_disk, entries)
            or 0xFFFFFFFF in (directory_size, directory_offset)):
        raise ValueError("zip64 archives are not accepted")
    if disk or directory_disk or entries_disk != entries:
        raise ValueError("multi-disk archives are not accepted")
    if directory_offset + directory_size != end:
        raise ValueError("the central directory does not end at the end record")
    records, position, names = [], directory_offset, set()
    for _ in range(entries):
        if data[position:position + 4] != b"PK\x01\x02":
            raise ValueError("bad central directory")
        flags = struct.unpack_from("<H", data, position + 8)[0]
        compressed, uncompressed, name_length, extra_length, comment_length = \
            struct.unpack_from("<IIHHH", data, position + 20)
        disk_start = struct.unpack_from("<H", data, position + 34)[0]
        local = struct.unpack_from("<I", data, position + 42)[0]
        raw_name = data[position + 46:position + 46 + name_length]
        name = raw_name.decode("utf-8" if flags & 0x800 else "cp437")
        extra = data[position + 46 + name_length:position + 46 + name_length + extra_length]
        if 0xFFFFFFFF in (compressed, uncompressed, local) or disk_start or _has_zip64_extra(extra):
            raise ValueError(f"zip64 entry not accepted: {name}")
        if name in names:
            raise ValueError(f"duplicate name in archive: {name}")
        names.add(name)
        if data[local:local + 4] != b"PK\x03\x04" or local >= directory_offset:
            raise ValueError(f"bad local header for {name}")
        local_name_length, local_extra_length = struct.unpack_from("<HH", data, local + 26)
        if data[local + 30:local + 30 + local_name_length] != raw_name:
            raise ValueError(f"local and central names differ for {name}")
        local_extra = data[local + 30 + local_name_length:local + 30 + local_name_length + local_extra_length]
        if _has_zip64_extra(local_extra):
            raise ValueError(f"zip64 entry not accepted: {name}")
        size = 46 + name_length + extra_length + comment_length
        records.append({"name": name, "position": position, "size": size, "local": local})
        position += size
    if position != end:
        raise ValueError("the central directory has data after its records")
    if len({record["local"] for record in records}) != len(records):
        raise ValueError("two entries share one local header")
    seen = [(info.filename, info.header_offset) for info in zipfile.ZipFile(io.BytesIO(data)).infolist()]
    if seen != [(record["name"], record["local"]) for record in records]:
        raise ValueError("zipfile reads other entries than the central directory holds")
    return records, end


def _has_zip64_extra(extra):
    position = 0
    while position + 4 <= len(extra):
        header, length = struct.unpack_from("<HH", extra, position)
        if header == 0x0001:
            return True
        position += 4 + length
    return False


def content_hash(data):
    """NuGet's package content hash (the sha512 a deps.json records): SHA-512
    of the .nupkg without nuget.org's repository signature entry, with the
    central directory's offsets and counts adjusted as if it had never been
    there (NuGet.Packaging SignedPackageArchiveUtility.GetPackageContentHash).
    Unsigned packages hash as they are. Raises ValueError for an archive
    archive_records refuses."""
    records, end = archive_records(data)
    entries_disk, entries, directory_size, directory_offset = struct.unpack_from("<HHII", data, end + 8)
    by_offset = sorted(records, key=lambda record: record["local"])
    for record, following in zip(by_offset, by_offset[1:] + [None]):
        record["entry_size"] = (following["local"] if following else directory_offset) - record["local"]
    signature = next((record for record in records if record["name"] == SIGNATURE), None)
    if signature is None:
        return base64.b64encode(hashlib.sha512(data).digest()).decode("ascii")
    h = hashlib.sha512()
    kept = [record for record in by_offset if record is not signature]
    h.update(data[:by_offset[0]["local"]])
    for record in kept:
        h.update(data[record["local"]:record["local"] + record["entry_size"]])
    for record in sorted(kept, key=lambda record: record["position"]):
        shift = signature["entry_size"] if record["local"] > signature["local"] else 0
        start = record["position"]
        h.update(data[start:start + 42])
        h.update(struct.pack("<I", record["local"] - shift))
        h.update(data[start + 46:start + record["size"]])
    h.update(data[end:end + 8])
    h.update(struct.pack("<HHII", entries_disk - 1, entries - 1, directory_size - signature["size"],
                         directory_offset - signature["entry_size"]))
    h.update(data[end + 20:])
    return base64.b64encode(h.digest()).decode("ascii")


def checked_hash(data, source):
    try:
        return content_hash(data)
    except (ValueError, zipfile.BadZipFile, struct.error, UnicodeDecodeError) as error:
        raise SystemExit(f"{source}: {error}; nothing was kept")


def fetch_package(url, path, content_sha512, opener=None, say=print):
    """Downloads a .nupkg unless the copy at path has the content hash; keeps
    it only when the hash matches."""
    import urllib.request

    path = Path(path)
    if path.is_file():
        try:
            if content_hash(path.read_bytes()) == content_sha512:
                say(f"  have {path.name} (hash ok)")
                return path
        except (ValueError, zipfile.BadZipFile, struct.error, UnicodeDecodeError):
            pass  # fetched again below
    say(f"  downloading {url}")
    with (opener or urllib.request.urlopen)(url) as response:
        data = response.read()
    if checked_hash(data, url) != content_sha512:
        raise SystemExit(f"hash mismatch for {url}; nothing was kept")
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_name(path.name + ".part")
    part.write_bytes(data)
    part.replace(path)
    return path


def load_manifest(manifest=MANIFEST):
    return json.loads(Path(manifest).read_text(encoding="utf-8"))


def install(host_core=HOST_CORE, downloads=DOWNLOADS, fetch=fetch_package, say=print, manifest=MANIFEST):
    """Fetches, checks and extracts every package; returns the files written."""
    host_core = Path(host_core)
    deps_file = host_core / "GwentHost.Core.deps.json"
    if not deps_file.is_file():
        raise SystemExit(f"No {deps_file}; extract the complete release (or build GwentHost.Core)")
    deps = json.loads(deps_file.read_text(encoding="utf-8"))
    pins = load_manifest(manifest)
    listed = packages(deps)
    # Every package and file first: nothing is written for a release whose
    # deps.json names a package or file this project has not pinned.
    for name, version, _, files in listed:
        pinned = pins.get(f"{name}/{version}")
        if pinned is None:
            raise SystemExit(f"{name} {version} is not pinned in {Path(manifest).name}; "
                             "this host-core does not match these scripts (extract the complete release again)")
        if missing := sorted(set(files) - set(pinned)):
            raise SystemExit(f"{name} {version}: {', '.join(missing)} not pinned in {Path(manifest).name}")
    written = []
    for name, version, sha512, files in listed:
        lower = name.lower()
        package = fetch(f"{FEED}/{lower}/{version}/{lower}.{version}.nupkg",
                        Path(downloads) / f"{lower}.{version}.nupkg", sha512, say=say)
        # Checked again on the bytes that are extracted, whoever fetched them.
        data = Path(package).read_bytes()
        if checked_hash(data, package) != sha512:
            raise SystemExit(f"hash mismatch for {package}; nothing was extracted")
        pinned = pins[f"{name}/{version}"]
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            contents = {}
            for inside, relative in files.items():
                target = (host_core / relative).resolve()
                if not target.is_relative_to(host_core.resolve()):
                    raise SystemExit(f"{name}: unsafe path {relative}")
                content = archive.read(inside)
                if hashlib.sha256(content).hexdigest() != pinned[inside]:
                    raise SystemExit(f"{name} {version}: {inside} is not the pinned file; nothing was extracted")
                contents[target] = content
        for target, content in contents.items():
            if not target.is_file() or target.read_bytes() != content:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)
            written.append(target)
    say(f"  host-core: {len(written)} package files from {len(listed)} NuGet packages")
    return written


def nuget_packages_folder():
    return Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget" / "packages")


def pin(build, out=MANIFEST, packages_folder=None):
    """Writes the manifest for the packages of a local build (see the module
    docstring); returns it."""
    build = Path(build)
    folder = Path(packages_folder or nuget_packages_folder())
    deps = json.loads((build / "GwentHost.Core.deps.json").read_text(encoding="utf-8"))
    manifest = {}
    for name, version, sha512, files in packages(deps):
        package_dir = folder / name.lower() / version
        nupkg = package_dir / f"{name.lower()}.{version}.nupkg"
        if content_hash(nupkg.read_bytes()) != sha512:
            raise SystemExit(f"{nupkg} does not match the deps.json's SHA-512")
        entry = {}
        with zipfile.ZipFile(nupkg) as archive:
            for inside, relative in sorted(files.items()):
                content = archive.read(inside)
                if (build / relative).read_bytes() != content:
                    raise SystemExit(f"{build / relative} differs from {inside} in {nupkg.name}")
                entry[inside] = hashlib.sha256(content).hexdigest()
        manifest[f"{name}/{version}"] = entry
    Path(out).write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return manifest


if __name__ == "__main__":
    if sys.argv[1:2] == ["--pin"]:
        pinned = pin(Path(sys.argv[2]) if len(sys.argv) > 2 else HOST_CORE)
        print(f"pinned {sum(map(len, pinned.values()))} files from {len(pinned)} packages in {MANIFEST}")
    else:
        install(Path(sys.argv[1]) if len(sys.argv) > 1 else HOST_CORE)
