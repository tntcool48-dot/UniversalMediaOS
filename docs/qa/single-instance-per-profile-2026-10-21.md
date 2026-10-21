# One running app per profile — October 21, 2026

**User decision (October 21):** allow one running instance per profile; a second launch brings the existing window forward instead of starting another copy.

## Why

The app had no guard, so two copies could run on one profile:

- each loaded and rewrote the same `download-queue.json`, so a second launch marked the first copy's running job as interrupted and paused, and both copies picked the same oldest pending job;
- each wrote its whole settings map, so the last save won;
- each started its own services.

## Design

`ProfileInstanceGuard` (`UniversalMediaOS.WPF/Helpers`) holds a named mutex per profile, plus a request event and an acknowledgement event.

- The profile key is a hash of the normalized profile folder (`<data root>\Roaming\UniversalMediaOS`), so different spellings of one folder (case, trailing separator) are one profile. Different data roots, such as test fixtures or a portable root, stay independent.
- The decision comes first in `OnStartup`, before LibVLC, the log, the database or any service.
- **Running copy:** a later launch asks it to come forward and waits up to 5 s for the acknowledgement. The running copy restores its window if minimized, activates and raises it, and logs "[Startup] Another launch on this profile brought the running window forward." The later launch then exits with code 0, without logging or resolving any service. Its `OnExit` skips the service cleanup, which would otherwise construct and rewrite the queue.
- **Starting copy:** it acknowledges at once, because its window is about to show.
- **Closing copy:** from its first `Closing` (the progress drain), it does not acknowledge. The later launch waits up to 25 s for the profile, then starts normally. The owner releases the profile last in `OnExit`, after services, the player engine and the log have stopped.
- **Unresponsive copy:** if the running copy neither acknowledges nor closes, the later launch shows "already running for this profile but is not responding" and exits.
- **Exit:** disposal waits (up to 2 s) for a request already being answered before closing its events. A request that arrives while the copy exits is ignored instead of faulting a pool thread. The guard cases were re-run after this change, within the 55 and 20 related passes below.

## Verification

| Check | Result | TRX |
| --- | --- | --- |
| Guard unit cases: hand-off to a running copy; waiting for a closing copy (about 0.7 s) and then starting; giving up on an unresponsive copy; independent profiles and spelling-insensitive keys; a copy whose thread ended without releasing | **5 / 5**, 1 s | `profile-instance-guard-20261021.trx` |
| Same real-app case against the app built from the committed `App.xaml.cs` (no guard; the file was restored afterwards with a matching hash) | **Fail**: "The second launch kept running beside the first." The test then ended the second copy. | `single-instance-before-20261021.trx` |
| Real app, `T2_Settings_01_Duplicate_Service_Bootstrapping` (replaces the skipped placeholder): the running window is minimized, and a second process starts with the same data root. It exits with code 0 within 15 s, and the running window is restored. The log has the "brought forward" line and exactly one "Application session started." | **Pass**, 5 s | `single-instance-after-20261021.trx` |
| Tests that close and relaunch the app on one profile (Settings, My List identity, voice-cast, episode-notification and MAL-sync recovery, packaged resume durability), plus the guard cases | **20 passed**, with the 1 remaining Settings placeholder skipped, in 4 min 9 s | `single-instance-related-20261021.trx` |

Skipped methods in the suite drop from **11 to 10**: nine Downloads and one Settings (low memory).

## Limits

- The guard covers one Windows session (`Local\` names). Another signed-in session using the same profile folder is not detected.
- If two later launches arrive together, one may miss the acknowledgement; it then waits up to 25 s and reports the running copy as not responding.
- Window activation and taskbar behavior were checked through UI Automation, not by a person double-clicking the executable.
