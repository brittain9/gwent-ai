"""Build the two Windows session parts from source: platform/windows/session_net.c
(dist/session_net.dll, MinGW) and the GwentSessionNet plugin that loads it
(dist/GwentSessionNet.dll, .NET SDK). A downloaded release carries both;
scripts/build-local.sh builds them with the rest on Linux."""

import os
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
SESSION_NET_SOURCE = ROOT / "platform" / "windows" / "session_net.c"
PLUGIN_PROJECT = ROOT / "platform" / "windows" / "SessionNet" / "GwentSessionNet.csproj"
SESSION_NET = ROOT / "dist" / "session_net.dll"
PLUGIN = ROOT / "dist" / "GwentSessionNet.dll"


TARGET = "x86_64-w64-mingw32"  # the 64-bit client loads only a 64-bit DLL


def mingw(candidates=None, runner=subprocess.run):
    """(compiler, environment) for a 64-bit MinGW gcc, or None. A gcc whose
    target (gcc -dumpmachine) is not x86_64-w64-mingw32 (a 32-bit MinGW,
    Cygwin's or MSYS's own gcc) is passed over. MSYS2's gcc needs its own bin
    directory on PATH to find its helpers."""
    if candidates is None:
        candidates = [shutil.which("x86_64-w64-mingw32-gcc"), shutil.which("gcc"),
                      r"C:\msys64\mingw64\bin\gcc.exe", r"C:\msys64\ucrt64\bin\gcc.exe"]
    for candidate in candidates:
        if not candidate or not Path(candidate).is_file():
            continue
        env = dict(os.environ)
        env["PATH"] = str(Path(candidate).parent) + os.pathsep + env.get("PATH", "")
        try:
            machine = runner([candidate, "-dumpmachine"], capture_output=True, text=True, env=env).stdout.strip()
        except OSError:
            continue
        if machine == TARGET:
            return candidate, env
    return None


def stale(output, *sources):
    """True when output is missing or older than a source that exists."""
    output = Path(output)
    if not output.is_file():
        return True
    built = output.stat().st_mtime
    return any(Path(source).is_file() and Path(source).stat().st_mtime > built for source in sources)


def dotnet_sdk(runner=subprocess.run):
    """True when a .NET SDK is installed (the runtime alone cannot build)."""
    dotnet = shutil.which("dotnet")
    if not dotnet:
        return False
    try:
        return bool(runner([dotnet, "--list-sdks"], capture_output=True, text=True).stdout.strip())
    except OSError:
        return False


def needs_build(output, sources, compiler, checkout=None):
    """Whether setup builds output: when it is missing, or in a source
    checkout (a .git folder) when a source is newer and its compiler is here
    (compiler: a function, called only then). A downloaded release keeps the
    binaries it carries: their SHA-256 is the release's, and an extracted
    archive's file times say nothing about which is newer."""
    if not Path(output).is_file():
        return True
    checkout = (ROOT / ".git").exists() if checkout is None else checkout
    return checkout and stale(output, *sources) and bool(compiler())


def build_session_net(output=SESSION_NET):
    found = mingw()
    if not found:
        raise SystemExit("No 64-bit MinGW compiler (a gcc targeting x86_64-w64-mingw32); install MSYS2's "
                         "mingw-w64-x86_64-gcc "
                         "or use a downloaded release, which carries dist/session_net.dll")
    cc, env = found
    Path(output).parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([cc, "-O2", "-Wall", "-Wextra", "-shared", str(SESSION_NET_SOURCE), "-o", str(output),
                    "-lws2_32", "-lpsapi", "-lcrypt32", "-s", "-Wl,--no-insert-timestamp", "-Wl,--disable-auto-image-base"], check=True, env=env)
    return Path(output)


def build_plugin(client, loader):
    if not shutil.which("dotnet"):
        raise SystemExit("No dotnet; install the .NET 10 SDK, or use a downloaded release, "
                         "which carries dist/GwentSessionNet.dll")
    subprocess.run(["dotnet", "build", str(PLUGIN_PROJECT), "-c", "Release", "--nologo", "-v", "quiet",
                    f"-p:ClientDir={client}", f"-p:LoaderDir={loader}"], check=True)
    return PLUGIN
