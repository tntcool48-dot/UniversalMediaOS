# Downloads queue journeys in the real app — October 22, 2026

Replaces four skipped Downloads placeholders from the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **0be293c**.

## Setup

Each case starts its own local seeder: a generated episode of about 6.6 MB, served through a loopback tracker and peer with a capped upload rate. Each case also starts its own AppFixture profile on the second monitor. The profile is set up so that:

- the app's Nyaa and AnimeTosho feeds point at the seeder;
- qBittorrent points at a port that answers 503, so the native engine runs the job;
- the download folder is inside the sandbox;
- the profile's queue file holds one pending season job.

No live provider or outside network is involved.

## Cases

| Test | Journey | Result |
| --- | --- | --- |
| `T1_Downloads_02_Pause_and_Resume_Download` | At 64 KB/s, the row shows Downloading with rising progress. **Pause** → Paused: the partial data stays, progress does not move, and over 3 s the seeder sends at most one in-flight piece. **Resume** → Downloading, with progress above the paused value. | Pass |
| `T1_Downloads_03_Download_Progress_Updates` | At 1 MB/s, the row shows at least three distinct progress values and completes. The finished episode then opens in the player ("Queue Space Fixture - Episode 1"), because play-after-download is on by default. Back in Downloads, the row shows Completed at 100%, and the library holds the full-size episode. | Pass |
| `T1_Downloads_04_Cancel_Download_and_Clean_Files` | At 64 KB/s, **Cancel** asks "Cancel Download"; Yes → Cancelled, "partial files were retained". The partial data stays, the seeder stops sending, Retry and Remove are enabled, and Pause is disabled. | Pass |
| `T2_Downloads_05_Interrupted_Download_Recovery` | At 64 KB/s, past 1% the app closes normally and relaunches. The job is Paused with a resume message, and its partial is not smaller. **Resume** continues from that partial, which grows. | Pass |

**Cancelling keeps files.** The placeholder's name promises "clean files", but the product deliberately keeps partial files on cancel so a retry can reuse them, and both the confirmation and the status say so. The test asserts that designed behavior; it does not delete anything.

**First runs.** The first run passed pause/resume and restart recovery, and failed two cases on test assumptions (`downloads-queue-journeys-first-20261022.trx`):

- the cancel confirmation was not answered;
- the default play-after-download switched to the player before the test read "Completed".

Both are now part of the journeys, and the two cases passed (`downloads-queue-journeys-second-20261022.trx`).

## Verification

The whole Downloads class passes **5** (the four journeys and the existing refresh check), with five placeholders still skipped, in 1 min 51 s (`downloads-queue-journeys-20261022.trx`). Skipped methods in the suite drop from **10 to 6**: five Downloads and one Settings (low memory).

## Limits

- Only the native engine ran. The qBittorrent path is covered by the separate external-client journey, not here.
- One single-episode season over loopback. Long transfers, many seeders and real trackers are not exercised.
- The local seeder is the existing `NativeTemporaryDownloadBoundaryTests.LocalSeeder`, now shared (`internal`) for these cases.
