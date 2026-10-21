# Real Windows shutdown qualification — prepared, awaiting the user — October 21, 2026

A real sign-out or restart can only be done by the user; nothing here ends the Windows session. This batch prepares the run and the check, and dry-runs the check's mechanics.

## Tools

`tools/qualify-windows-shutdown.ps1` (Windows PowerShell 5.1, UI Automation) and `tools/qualify-windows-shutdown.py` (stdlib only, run with `-I`):

- **Prepare** creates a run folder under the git-ignored `.artifacts/implementation/`. It holds an isolated profile (second monitor, services off, debug log, database and downloads inside the run) and a generated **two-hour** film with `library-item.json` (`av:fixture:film:windows-shutdown / feature`). Two hours means a delayed sign-out cannot reach the end and clear the position as completed.
- **Start** launches the current build with that profile and opens the film through the real Player UI: Player, Playback options, volume 0, source path, Open. It waits for the playing event and writes `receipt.json` (PID, process start, executable SHA-256, source HEAD, uncommitted-changes flag). It also starts a hidden heartbeat that writes the time every 250 ms; its last write marks when Windows ended the session, even if the app's background log was not flushed.
- **Check** (after signing back in) does three things:
  - It refuses to run while the started app still runs, and refuses to repeat itself once a result exists.
  - It reads a copy of the database, the journal and the ended session's log. It passes only if the saved position (journal first, else SQLite) is within **3 s** of where uninterrupted playback was at session end: the session-end log line if flushed, otherwise the last heartbeat. A periodic save alone can trail by up to 10 s.
  - It then relaunches the app, reopens the film, requires "Restored … to X s" to equal that saved position, and closes the app normally through its window.
- **Stop** abandons a started run without ending the session.

## Dry run of the mechanics (not a session end)

Run folder `windows-shutdown-dryrun-20261021`, source **aaa50ec**, executable SHA-256 `A7F6EFE9…46E5`. The film played on the second monitor from 03:01:54. After about 22 s the test **killed** the app and heartbeat; this is a deliberate stand-in, not a Windows session end. Check correctly reported **FAIL**:

- no session-end line;
- last heartbeat 03:02:16.088, so the expected position was **22.088 s**;
- the saved position was the last periodic save, **15.101 s**, outside the 3 s tolerance. The periodic save's own stamp (15.1 s at 15 s after start) confirms the calibration.

The relaunch restored exactly **15.1 s**, matching the saved value. The dry run's video was removed afterwards; its receipt, result, log and database remain.

The first Prepare attempt failed because Windows PowerShell 5.1 has no `$PSScriptRoot` while evaluating parameter defaults; the default run folder is now set in the script body.

## Prepared for the user

`.artifacts/implementation/windows-shutdown-run` is prepared (film 59,261,824 bytes) but not started. Protocol:

1. Run Start (`powershell -ExecutionPolicy Bypass -File tools\qualify-windows-shutdown.ps1 -Mode Start`) and wait for "The film is playing on the second monitor".
2. Do not pause or seek. Sign out, or restart Windows, within two hours.
3. After signing back in, run `-Mode Check`; its result is saved as `check-result.json` in the run folder.

## Limits

This qualifies one sign-out or restart with the Debug build of the recorded source, not a packaged release. Windows decides how long it waits after WM_QUERYENDSESSION, and the app caps its own barrier at 3 s. Shutdown under a held SQLite writer is covered by automated tests, not by this real run.
