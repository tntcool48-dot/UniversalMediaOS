"""Qualifies the packaged app's Python service against one interpreter, in a profile of its own.

Usage:
  python -I tools/qualify-python-runtime.py --exe <UniversalMediaOS.WPF.exe> --python <folder holding python.exe>
         --run <new folder> [--pip-index default|unreachable|silent] [--close-after <seconds>] [--timeout <seconds>]

The app starts with services on, PATH limited to that folder and Windows' own folders, the pinned uBlock
manifest already installed (no extension download) and qBittorrent pointed at an invalid host. --pip-index
points pip at a loopback port nothing listens on, or at one that accepts and never answers. The script waits
for Python preparation to settle, or closes the window after --close-after seconds while it is still running,
then closes the main window as its title-bar button does and prints one JSON object: the interpreter, each
preparation state the app logged with its time, whether the deployed scripts match the package, the pinned
packages now installed, how long the close took, and any interpreter process still running afterwards
(which it then stops).
"""

import argparse
import ctypes
import ctypes.wintypes
import hashlib
import json
import os
import re
import socket
import subprocess
import threading
import time
from pathlib import Path

LINE = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\] \[([A-Z]+)\] \[Python\] Preparation (\w+) \(([a-z_]+)\)(?:: (.*))?$")
SETTLED = {"Ready", "Failed", "Canceled"}
PINS = {"drissionpage": "4.1.1.4", "curl-cffi": "0.15.0", "httpx": "0.28.1", "beautifulsoup4": "4.15.0", "lxml": "6.1.1"}
SCRIPTS = ("scraper.py", "book_scraper.py", "audiovisual_scraper.py")


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def silent_index():
    """A loopback server that accepts every connection and never answers, like a stalled package index."""
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen()
    held = []

    def accept():
        while True:
            try:
                held.append(server.accept()[0])
            except OSError:
                return
    threading.Thread(target=accept, daemon=True).start()
    return server, held


def prepare_profile(profile):
    roaming = profile / "Roaming" / "UniversalMediaOS"
    roaming.mkdir(parents=True)
    (roaming / "config.json").write_text(json.dumps({
        "AutoManageServices": "true", "EnableDebugLogging": "true", "StartupMonitor": "Secondary",
        "QBitHost": "qbittorrent.invalid"}), encoding="utf-8")
    extension = profile / "Local" / "UniversalMediaOS" / "Extensions" / "ublock-origin"
    extension.mkdir(parents=True)
    (extension / "manifest.json").write_text('{"version":"1.71.0"}', encoding="utf-8")
    return roaming / "app.log"


def preparation_states(log):
    if not log.exists():
        return []
    text = log.read_text(encoding="utf-8", errors="replace")
    return [{"logged": m.group(1), "state": m.group(3), "code": m.group(4), "missing": m.group(5) or ""}
            for m in map(LINE.match, text.splitlines()) if m]


def main_window(pid):
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(ctypes.wintypes.BOOL, ctypes.wintypes.HWND, ctypes.wintypes.LPARAM)
    def visit(hwnd, _):
        owner = ctypes.wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        title = ctypes.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, title, 256)
        if owner.value == pid and title.value == "Universal Media OS" and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True
    user32.EnumWindows(visit, 0)
    return found[0] if found else None


def interpreter_processes(folder):
    command = ("Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith("
               f"'{str(folder).replace(chr(39), chr(39) * 2)}', 'OrdinalIgnoreCase') }} | "
               "Select-Object ProcessId, CommandLine | ConvertTo-Json -Compress")
    out = subprocess.run(["powershell", "-NoProfile", "-Command", command], capture_output=True, text=True).stdout.strip()
    if not out:
        return []
    rows = json.loads(out)
    return rows if isinstance(rows, list) else [rows]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--python", required=True, type=Path)
    parser.add_argument("--run", required=True, type=Path)
    parser.add_argument("--pip-index", choices=("default", "unreachable", "silent"), default="default")
    parser.add_argument("--close-after", type=float)
    parser.add_argument("--timeout", type=float, default=240)
    args = parser.parse_args()
    exe, python_dir, run = args.exe.resolve(), args.python.resolve(), args.run.resolve()
    run.mkdir(parents=True, exist_ok=False)
    profile = run / "profile"
    log = prepare_profile(profile)
    interpreter = python_dir / "python.exe"
    version = subprocess.run([str(interpreter), "--version"], capture_output=True, text=True).stdout.strip()

    environment = {k: v for k, v in os.environ.items()
                   if k.upper() not in ("PYTHONHOME", "PYTHONPATH", "VIRTUAL_ENV", "PIP_INDEX_URL", "PIP_EXTRA_INDEX_URL")}
    windows = os.environ.get("SystemRoot", r"C:\Windows")
    environment["PATH"] = ";".join([str(python_dir), str(Path(windows) / "System32"), windows])
    environment["UNIVERSAL_MEDIA_OS_DATA_ROOT"] = str(profile)
    index = None
    if args.pip_index == "unreachable":
        environment["PIP_INDEX_URL"] = f"http://127.0.0.1:{free_port()}/simple"
    elif args.pip_index == "silent":
        index, _held = silent_index()
        environment["PIP_INDEX_URL"] = f"http://127.0.0.1:{index.getsockname()[1]}/simple"

    launched = time.monotonic()
    app = subprocess.Popen([str(exe)], cwd=str(exe.parent), env=environment)
    seen = []
    deadline = launched + (args.close_after if args.close_after else args.timeout)
    while time.monotonic() < deadline and app.poll() is None:
        states = preparation_states(log)
        for state in states[len(seen):]:
            state["seconds"] = round(time.monotonic() - launched, 1)
            seen.append(state)
        if not args.close_after and seen and seen[-1]["state"] in SETTLED:
            break
        time.sleep(0.25)
    running_before_close = [p["CommandLine"] for p in interpreter_processes(python_dir)]

    window = main_window(app.pid)
    close_started = time.monotonic()
    if window:
        ctypes.windll.user32.PostMessageW(window, 0x0010, 0, 0)  # WM_CLOSE, as the title-bar button.
    try:
        app.wait(30)
        closed = {"normally": True, "seconds": round(time.monotonic() - close_started, 2), "exitCode": app.returncode}
    except subprocess.TimeoutExpired:
        app.kill()
        closed = {"normally": False, "seconds": None, "exitCode": None}
    time.sleep(1)
    left = interpreter_processes(python_dir)
    for process in left:
        subprocess.run(["taskkill", "/PID", str(process["ProcessId"]), "/T", "/F"], capture_output=True)
    if index:
        index.close()

    for state in preparation_states(log)[len(seen):]:
        seen.append(state)
    listed = subprocess.run([str(interpreter), "-m", "pip", "list", "--format=json", "--disable-pip-version-check"],
                            capture_output=True, text=True).stdout
    installed = {p["name"].lower().replace("_", "-"): p["version"] for p in json.loads(listed or "[]")}
    services = profile / "Local" / "UniversalMediaOS" / "Services"
    print(json.dumps({
        "interpreter": version, "pipIndex": args.pip_index, "states": seen,
        "deployedScriptsMatchPackage": all(digest(services / s) == digest(exe.parent / s) for s in SCRIPTS),
        "pins": {name: installed.get(name) for name in PINS},
        "pinsMatch": all(installed.get(name) == pin for name, pin in PINS.items()),
        "interpreterProcessesBeforeClose": running_before_close, "close": closed,
        "interpreterProcessesLeftAfterExit": [p["CommandLine"] for p in left],
    }, indent=2))


if __name__ == "__main__":
    main()
