#!/usr/bin/env python3
"""A per-install TLS certificate for the local server, in pure Python (no
OpenSSL on Windows players' machines).

    python scripts/selfsigned.py DIR     # writes DIR/fake.crt and DIR/fake.key

An end-entity certificate, never a certificate authority: basicConstraints
CA:FALSE, key usage digitalSignature and keyEncipherment, extended key usage
serverAuth, and only the names the local server answers: the GOG host list of
server/gog_hosts.py, localhost and 127.0.0.1; no wildcard, so
a user who trusts it (scripts\\setup.cmd --trust-cert) trusts it for those
names alone. Its RSA-2048 key is made here from the operating system's random
source and never leaves this install; on Windows its file's access list grants
the current user alone (icacls; chmod does not apply there). (The upstream
server shipped one CA certificate for every user, whose key is public; see
server/UPSTREAM.md.) The file names are those the server and nginx read
(GWENT_CERT_DIR).
"""

import base64
import csv
import datetime
import hashlib
from pathlib import Path
import secrets
import subprocess
import sys

ADDRESSES = (bytes([127, 0, 0, 1]),)
COMMON_NAME = "Gwent AI local server"
VALID_DAYS = 3 * 365
PUBLIC_EXPONENT = 65537

# --- RSA ---------------------------------------------------------------------

SMALL_PRIMES = [p for p in range(3, 2000) if all(p % d for d in range(2, int(p ** 0.5) + 1))]


def probably_prime(n, rounds=40):
    if n < 2:
        return False
    for p in SMALL_PRIMES:
        if n % p == 0:
            return n == p
    d, s = n - 1, 0
    while d % 2 == 0:
        d //= 2
        s += 1
    for _ in range(rounds):
        a = secrets.randbelow(n - 3) + 2
        x = pow(a, d, n)
        if x in (1, n - 1):
            continue
        for _ in range(s - 1):
            x = pow(x, 2, n)
            if x == n - 1:
                break
        else:
            return False
    return True


def random_prime(bits):
    while True:
        # The top two bits set make the product exactly 2 * bits long.
        candidate = secrets.randbits(bits) | (3 << (bits - 2)) | 1
        if (candidate - 1) % PUBLIC_EXPONENT and probably_prime(candidate):
            return candidate


def rsa_key(bits=2048):
    while True:
        p, q = random_prime(bits // 2), random_prime(bits // 2)
        if p == q:
            continue
        n = p * q
        if n.bit_length() != bits:
            continue
        d = pow(PUBLIC_EXPONENT, -1, (p - 1) * (q - 1))
        return {"n": n, "e": PUBLIC_EXPONENT, "d": d, "p": p, "q": q,
                "dp": d % (p - 1), "dq": d % (q - 1), "qinv": pow(q, -1, p)}


def sign_sha256(key, message):
    """RSASSA-PKCS1-v1_5 with SHA-256 (RFC 8017, 8.2.1)."""
    digest_info = bytes.fromhex("3031300d060960864801650304020105000420") + hashlib.sha256(message).digest()
    size = (key["n"].bit_length() + 7) // 8
    encoded = b"\x00\x01" + b"\xff" * (size - len(digest_info) - 3) + b"\x00" + digest_info
    m = int.from_bytes(encoded, "big")
    # CRT, checked against the public key (a fault would leak the key).
    s1, s2 = pow(m, key["dp"], key["p"]), pow(m, key["dq"], key["q"])
    s = s2 + key["q"] * ((key["qinv"] * (s1 - s2)) % key["p"])
    if pow(s, key["e"], key["n"]) != m:
        raise RuntimeError("RSA signature check failed")
    return s.to_bytes(size, "big")


# --- DER ---------------------------------------------------------------------

def der(tag, content):
    length = len(content)
    if length < 0x80:
        header = bytes([length])
    else:
        octets = length.to_bytes((length.bit_length() + 7) // 8, "big")
        header = bytes([0x80 | len(octets)]) + octets
    return bytes([tag]) + header + content


def sequence(*items):
    return der(0x30, b"".join(items))


def integer(value):
    return der(0x02, value.to_bytes(value.bit_length() // 8 + 1, "big"))


def oid(dotted):
    parts = [int(part) for part in dotted.split(".")]
    body = bytes([40 * parts[0] + parts[1]])
    for part in parts[2:]:
        chunk = [part & 0x7F]
        part >>= 7
        while part:
            chunk.append(0x80 | (part & 0x7F))
            part >>= 7
        body += bytes(reversed(chunk))
    return der(0x06, body)


def bit_string(data, unused=0):
    return der(0x03, bytes([unused]) + data)


def octet_string(data):
    return der(0x04, data)


def utc_time(moment):
    return der(0x17, moment.strftime("%y%m%d%H%M%SZ").encode("ascii"))


NULL = b"\x05\x00"
TRUE = b"\x01\x01\xff"
SHA256_WITH_RSA = sequence(oid("1.2.840.113549.1.1.11"), NULL)


def name(common_name):
    return sequence(der(0x31, sequence(oid("2.5.4.3"), der(0x0C, common_name.encode("utf-8")))))


def extension(identifier, value, critical=False):
    return sequence(oid(identifier), *([TRUE] if critical else []), octet_string(value))


def names():
    """The DNS names of the certificate: the GOG hosts the session routes to
    the local server, and localhost."""
    import windows_paths
    return (*windows_paths.gog_hosts(), "localhost")


def alternative_names(hosts=None):
    """The subjectAltName extension's value."""
    return sequence(*[der(0x82, host.encode("ascii")) for host in (hosts or names())],
                    *[der(0x87, address) for address in ADDRESSES])


def certificate(key, now=None, hosts=None):
    """The DER certificate for key."""
    now = now or datetime.datetime.now(datetime.timezone.utc)
    public_key = sequence(integer(key["n"]), integer(key["e"]))
    subject_key = sequence(sequence(oid("1.2.840.113549.1.1.1"), NULL), bit_string(public_key))
    extensions = sequence(
        extension("2.5.29.19", sequence(), critical=True),                   # basicConstraints: CA:FALSE
        extension("2.5.29.15", bit_string(b"\xa0", unused=5), critical=True),  # digitalSignature, keyEncipherment
        extension("2.5.29.37", sequence(oid("1.3.6.1.5.5.7.3.1"))),           # serverAuth
        extension("2.5.29.17", alternative_names(hosts)),
        extension("2.5.29.14", octet_string(hashlib.sha1(public_key).digest())),
    )
    serial = int.from_bytes(secrets.token_bytes(16), "big") >> 1 or 1
    tbs = sequence(
        der(0xA0, integer(2)),  # version 3
        integer(serial),
        SHA256_WITH_RSA,
        name(COMMON_NAME),
        sequence(utc_time(now - datetime.timedelta(days=1)), utc_time(now + datetime.timedelta(days=VALID_DAYS))),
        name(COMMON_NAME),
        subject_key,
        der(0xA3, extensions),
    )
    return sequence(tbs, SHA256_WITH_RSA, bit_string(sign_sha256(key, tbs)))


def private_key_der(key):
    """PKCS #1 RSAPrivateKey."""
    return sequence(*(integer(value) for value in
                      (0, key["n"], key["e"], key["d"], key["p"], key["q"], key["dp"], key["dq"], key["qinv"])))


def pem(label, data):
    text = base64.b64encode(data).decode("ascii")
    lines = [text[i:i + 64] for i in range(0, len(text), 64)]
    return f"-----BEGIN {label}-----\n" + "\n".join(lines) + f"\n-----END {label}-----\n"


def pem_der(text):
    """The DER bytes of the first PEM block in text."""
    body = text.split("-----", 4)[2]
    return base64.b64decode("".join(body.split()))


def system_tool(name):
    """Windows' own tool, not one of the same name earlier on PATH (Git for
    Windows' whoami)."""
    import os
    return str(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / f"{name}.exe")


def current_user_sid(runner=subprocess.run):
    """The current Windows user's SID (whoami's CSV output, the same in every
    display language)."""
    result = runner([system_tool("whoami"), "/user", "/fo", "csv", "/nh"], capture_output=True, text=True)
    rows = list(csv.reader((result.stdout or "").splitlines()))
    sid = rows[0][-1].strip() if result.returncode == 0 and rows and rows[0] else ""
    if not sid.startswith("S-1-"):
        raise SystemExit(f"could not read your Windows user's SID (whoami /user): {result.stdout}{result.stderr}")
    return sid


def owner_only(path, runner=subprocess.run):
    """Restricts a file to its owner: 0600 on Linux and macOS; on Windows an
    access list granting the current user alone, without inherited entries
    (icacls; needs no administrator rights on one's own file)."""
    path = Path(path)
    if sys.platform != "win32":
        path.chmod(0o600)
        return
    result = runner([system_tool("icacls"), str(path), "/inheritance:r", "/grant:r", f"*{current_user_sid(runner)}:(F)"],
                    capture_output=True, text=True)
    if result.returncode:
        raise SystemExit(f"icacls could not restrict {path} to your user: {result.stdout}{result.stderr}")


def write(directory, hosts=None):
    """Writes directory/fake.crt and fake.key; returns the certificate's DER.
    The key file is restricted to its owner before the key is written."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    key = rsa_key()
    cert = certificate(key, hosts=hosts)
    key_path = directory / "fake.key"
    temporary = key_path.with_name("fake.key.part")
    temporary.unlink(missing_ok=True)
    temporary.touch()
    try:
        owner_only(temporary)
        temporary.write_text(pem("RSA PRIVATE KEY", private_key_der(key)), encoding="ascii", newline="\n")
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise
    temporary.replace(key_path)
    (directory / "fake.crt").write_text(pem("CERTIFICATE", cert), encoding="ascii", newline="\n")
    return cert


def children(data):
    """The (tag, content) elements inside DER content."""
    items, i = [], 0
    while i < len(data):
        tag, length = data[i], data[i + 1]
        i += 2
        if length & 0x80:
            count = length & 0x7F
            length = int.from_bytes(data[i:i + count], "big")
            i += count
        items.append((tag, data[i:i + length]))
        i += length
    return items


def not_after(cert_der):
    """The certificate's expiry (UTC), read from its DER."""
    tbs = children(children(cert_der)[0][1])[0][1]
    fields = children(tbs)
    if fields[0][0] == 0xA0:  # explicit version
        fields = fields[1:]
    tag, value = children(fields[3][1])[1]  # serial, signature, issuer, validity
    pattern = "%y%m%d%H%M%SZ" if tag == 0x17 else "%Y%m%d%H%M%SZ"
    return datetime.datetime.strptime(value.decode("ascii"), pattern).replace(tzinfo=datetime.timezone.utc)


def is_ours(cert_der):
    """True for a certificate this module made (CA:FALSE, our common name)."""
    return COMMON_NAME.encode("utf-8") in cert_der and b"\x06\x03\x55\x1d\x13\x01\x01\xff\x04\x02\x30\x00" in cert_der


def ensure(directory, min_days=30, hosts=None):
    """Keeps a valid certificate of ours in directory; makes a new one when it
    is missing, is not ours (an older CA), names other hosts than it should (an
    older wildcard one) or expires within min_days. Returns True when it made
    one."""
    path = Path(directory) / "fake.crt"
    if path.is_file() and (Path(directory) / "fake.key").is_file():
        try:
            cert = pem_der(path.read_text(encoding="ascii"))
            remaining = not_after(cert) - datetime.datetime.now(datetime.timezone.utc)
            if (is_ours(cert) and alternative_names(hosts) in cert
                    and remaining > datetime.timedelta(days=min_days)):
                return False
        except (ValueError, IndexError, UnicodeDecodeError):
            pass  # unreadable: replaced below
    write(directory, hosts)
    return True


def thumbprint(cert_der):
    """The SHA-1 thumbprint Windows certificate stores index by."""
    return hashlib.sha1(cert_der).hexdigest().upper()


def sha256(cert_der):
    """The SHA-256 of the certificate, which the game process accepts as this
    install's (GWENT_SESSION_CERT_SHA256, platform/windows/session_net.c)."""
    return hashlib.sha256(cert_der).hexdigest().upper()


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    write(sys.argv[1])
    print(f"wrote {Path(sys.argv[1]) / 'fake.crt'} and fake.key")
