"""Downloads pinned by hash: a file is kept only when its digest matches, and
a matching file already present is not fetched again."""

import base64
import hashlib
from pathlib import Path
import urllib.request


def digest(path, algorithm="sha256"):
    h = hashlib.new(algorithm)
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            h.update(chunk)
    return h


def matches(path, sha256=None, sha512_base64=None):
    path = Path(path)
    if not path.is_file():
        return False
    if sha256 is not None:
        return digest(path).hexdigest() == sha256.lower()
    if sha512_base64 is not None:
        return base64.b64encode(digest(path, "sha512").digest()).decode("ascii") == sha512_base64
    raise ValueError("a pinned download needs a hash")


def fetch(url, path, sha256=None, sha512_base64=None, opener=urllib.request.urlopen, say=print):
    """Downloads url to path unless a file with the pinned hash is there;
    raises SystemExit on a hash mismatch (nothing is kept)."""
    path = Path(path)
    if matches(path, sha256, sha512_base64):
        say(f"  have {path.name} (hash ok)")
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_name(path.name + ".part")
    say(f"  downloading {url}")
    with opener(url) as response, open(part, "wb") as stream:
        for chunk in iter(lambda: response.read(1 << 20), b""):
            stream.write(chunk)
    if not matches(part, sha256, sha512_base64):
        part.unlink()
        raise SystemExit(f"hash mismatch for {url}; nothing was kept")
    part.replace(path)
    return path
