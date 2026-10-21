"""Reads what a real Windows sign-out or restart left behind for tools/qualify-windows-shutdown.ps1.

Usage: python -I tools/qualify-windows-shutdown.py <run folder>

Prints one JSON object. The saved position must match where uninterrupted playback was when the
session ended: the session-end log line if the app flushed it, otherwise the last heartbeat. A periodic
save alone can trail by up to ten seconds, so a close match shows the session-end capture ran.
"""

import json
import re
import shutil
import sqlite3
import sys
import tempfile
from datetime import datetime, timedelta
from pathlib import Path

LINE = re.compile(r"^\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\] \[([A-Z]+)\] (.*)$")
SESSION_END = (
    "[Resume] Accepted playback progress drained before Windows session end.",
    "[Resume] Windows session-end deadline elapsed; accepted playback progress is journaled and its database commit is still pending.",
    "[Resume] Windows session-end deadline elapsed with playback progress still pending and not journaled.",
)
TRANSPORT = ("TogglePlayPause command invoked", "Seeked ", "LoadMedia invoked", "Pausing media player")
TOLERANCE_SECONDS = 3.0  # One-second log stamps at both ends, plus decoder start-up.


def stamp(text):
    return datetime.strptime(text, "%Y-%m-%d %H:%M:%S")


def session_lines(log_text, launched):
    """Lines of the session started by the receipt's launch, up to the next session."""
    parsed = [(stamp(m.group(1)), m.group(2), m.group(3)) for m in map(LINE.match, log_text.splitlines()) if m]
    starts = [i for i, (when, _, text) in enumerate(parsed)
              if text == "Application session started." and when >= launched - timedelta(seconds=1)]
    if not starts:
        return []
    following = [i for i, (_, _, text) in enumerate(parsed) if text == "Application session started." and i > starts[0]]
    return parsed[starts[0]:following[0] if following else len(parsed)]


def read_database(database, work, unit):
    """Reads a copy, so a WAL left by the ended process is neither changed nor required to be writable."""
    if not database.exists():
        return None
    with tempfile.TemporaryDirectory() as scratch:
        copy = Path(scratch) / database.name
        for suffix in ("", "-wal", "-shm"):
            source = Path(str(database) + suffix)
            if source.exists():
                shutil.copy2(source, Path(str(copy) + suffix))
        connection = sqlite3.connect(copy)
        try:
            row = connection.execute("SELECT PositionSeconds FROM ResumeStates WHERE MediaId = ? AND EpisodeId = ?",
                                     (work, unit)).fetchone()
        finally:
            connection.close()
    return None if row is None else float(row[0])


def read_journal(database, work, unit):
    journal = Path(str(database) + ".resume-pending.json")
    if not journal.exists():
        return None
    document = json.loads(journal.read_text(encoding="utf-8"))
    for pending in document.get("Pending", []):
        if pending.get("WorkKey") == work and pending.get("UnitKey") == unit:
            return float(pending["Position"])
    return None


def main(run):
    receipt = json.loads((run / "receipt.json").read_text(encoding="utf-8-sig"))
    log_path, database = Path(receipt["Log"]), Path(receipt["Database"])
    work, unit, title = receipt["Work"], receipt["Unit"], receipt["Title"]
    lines = session_lines(log_path.read_text(encoding="utf-8", errors="replace"), stamp(receipt["Launched"]))

    playing = [when for when, _, text in lines if text == f"LibVLC playing event fired for: '{title}'"]
    play_start = playing[-1] if playing else None
    restored = [float(m.group(1)) for when, _, text in lines
                if (m := re.fullmatch(rf"\[Resume\] Restored '{re.escape(title)}' to (\d+(?:\.\d+)?)s\.", text))
                and play_start and when <= play_start]
    start_position = restored[-1] if restored else 0.0
    saves = [(when, float(m.group(1))) for when, _, text in lines
             if (m := re.fullmatch(rf"\[Resume\] Saved (?:durable )?position (\d+(?:\.\d+)?)s for media '{re.escape(work)}', "
                                   rf"episode '{re.escape(unit)}'\.", text))]
    manual = [text for when, _, text in lines if play_start and when > play_start and text.startswith(TRANSPORT)]
    ended = next(((when, text) for when, _, text in lines if text in SESSION_END), None)
    drain_failed = [text for _, _, text in lines if text.startswith("[Resume] Windows session-end progress drain failed")]
    app_close = [text for _, _, text in lines if "before application close" in text or "Application close deadline" in text]

    heartbeat = None
    beat_file = run / "heartbeat.txt"
    if beat_file.exists():
        heartbeat = datetime.fromisoformat(beat_file.read_text(encoding="utf-8").strip().replace("Z", "+00:00"))
        heartbeat = heartbeat.astimezone().replace(tzinfo=None)
    end_time = ended[0] if ended else heartbeat

    committed = read_database(database, work, unit)
    journaled = read_journal(database, work, unit)
    # A journaled observation is newer than the row and replays over it before the next read.
    effective = journaled if journaled is not None else committed
    expected = None
    if play_start and end_time and not manual:
        expected = start_position + (end_time - play_start).total_seconds()
    captured = expected is not None and effective is not None and abs(effective - expected) <= TOLERANCE_SECONDS
    passed = bool(effective) and (captured if expected is not None else ended is not None)

    print(json.dumps({
        "SessionEndLine": ended[1] if ended else None,
        "SessionEndLogged": ended[0].isoformat(sep=" ") if ended else None,
        "LastHeartbeat": heartbeat.isoformat(sep=" ", timespec="milliseconds") if heartbeat else None,
        "PlaybackStarted": play_start.isoformat(sep=" ") if play_start else None,
        "StartPosition": start_position,
        "ManualTransportAfterStart": manual,
        "LastPeriodicSave": {"Logged": saves[-1][0].isoformat(sep=" "), "Position": saves[-1][1]} if saves else None,
        "SessionEndDrainFailed": drain_failed,
        "ApplicationCloseLines": app_close,
        "DatabasePosition": committed,
        "JournaledPosition": journaled,
        "EffectivePosition": effective,
        "ExpectedPosition": round(expected, 3) if expected is not None else None,
        "ToleranceSeconds": TOLERANCE_SECONDS,
        "CapturedAtSessionEnd": captured,
        "Passed": passed,
    }, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit("Usage: python -I tools/qualify-windows-shutdown.py <run folder>")
    main(Path(sys.argv[1]))
