# Downloads storage and provider failures — October 23, 2026

Part of the open "live provider/storage failures and coexistence with permanent downloads" item, plus the five skipped Downloads placeholders from the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **claude/download-storage-98fdb6**, cut from **e744dcd**. Every case runs against loopback fixtures: a local seeder with its own tracker and RSS feed, or a loopback stand-in for qBittorrent's WebUI. Real-app cases each use their own isolated AppFixture profile. No user profile, library, permanent download or live provider was touched, and the shared system drive was never filled.

## Repairs

Each repair started from a regression that failed on the unchanged source.

| Problem | Before | Repair | After |
| --- | --- | --- | --- |
| **qBittorrent's file names were trusted.** The season combined each reported name with the download folder unchecked. | `..\outside\episode-1.avi`, an absolute path and a drive-rooted path each **completed** the season, naming the outside file as the episode to auto-play (3 / 3 failed: `completed=True`, path outside). | Every reported name must resolve strictly inside the download folder, with rooted names refused. Otherwise the job stops before validation: "qBittorrent reports a file outside the download folder; it was not checked or played. Its files were left in place." | 3 / 3 |
| **A refused torrent showed a generic failure.** The native refusal of a torrent with an outside entry was logged only. | The row read "Download failed; the original release selection and existing files were retained for retry", which suggests Retry can help. | The refusal is a typed failure that reaches the queue: "The torrent lists a file outside the download folder; it was not started." | 1 / 1 |
| **A malformed magnet blocked the season.** The best-seeded feed result was used even when its magnet had no valid v1 hash, or conflicted with the feed's hash. Every Retry searched again and failed the same way. | 5 forms (bad hex, short hash, no `xt`, an infoHash-only item, a conflicting hash) failed the job, although a usable release was listed. With only malformed results, Retry still failed after a usable release appeared. | Unusable results are logged and skipped. A duplicate keeps a consistent entry over a conflicting one. With none usable, the job fails with "No release found has a magnet link with a valid transfer hash" and pins nothing, so Retry searches again. | 6 / 6 |
| **A permanent season substituted the other audio.** With no result carrying the requested label, it fell back to any release. | A Dub job downloaded an unlabelled or `[Sub]` release; a Sub job downloaded an `[English Dub]` release (3 failed; 3 controls passed). | The temporary watch's rule now applies: Dub needs a Dub or dual-audio label, and Sub excludes Dub-only releases. Otherwise: "No Dub release found: no matching release is labelled Dub or Dual Audio. Nothing was downloaded; retry later or choose Sub." (and the Sub equivalent). Labels select releases; they do not prove the spoken language. | 6 / 6 |
| **An older qBittorrent transfer in another folder was unexplained.** The same release already in qBittorrent elsewhere, or reported moved while the app monitored it, was correctly left untouched. | The row showed only the generic "Download failed…" (2 / 2 failed). | Both now fail with "This qBittorrent transfer belongs to a different download folder; it was left untouched. Existing files were retained." No add, resume or native engine starts. | 2 / 2 |

## Qualified without a production change

- **Real transfers through folder aliases.** A real loopback native season transfer, configured through a junction and through a SUBST drive, lands in the physical folder. The job pins and reports that folder, and the file is byte-identical (SHA-256) to the seeded episode.
- **Active-write retargeting.**
  - Retargeting the configured junction to a foreign folder mid-transfer does not move the writes: the destination was pinned to the physical path. The transfer completes there, and the foreign folder keeps only its sentinel.
  - Moving the physical folder away, to put a junction to a foreign folder in its place, is refused by Windows mid-transfer. The job's writer lease (opened without sharing) and the engine's open partials prevent renaming the folder or any ancestor. The transfer then completes.

## Placeholders replaced by real-app journeys

| Placeholder | Journey | Result |
| --- | --- | --- |
| `T2_Downloads_01_Output_Path_Traversal_Attempt` | The seeder's torrent names a file outside every download folder. The row fails with "The torrent lists a file outside the download folder; it was not started.". Nothing appears outside or in the library, and Remove is offered. | Pass |
| `T2_Downloads_04_Malformed_Info_Hash_Magnet_Link` | Magnets reach the queue only through the feeds. The feed first lists only a malformed magnet: the row fails with the hash message and nothing is transferred. Then the usable release is listed below the better-seeded malformed one, and Retry downloads it. | Pass |
| `T1_Downloads_05_Audio_Language_Preference_Selection` | A Dub job whose only release is labelled `[Sub]` fails with "No Dub release found" and transfers nothing. Once that release is relabelled `[Dual Audio]`, Retry downloads it and the queue pins that title. | Pass |
| `T2_Downloads_02_Torrent_Client_Offline_on_Connect` | The season starts in a loopback qBittorrent stand-in and is paused there. With qBittorrent offline (connections refused), Resume fails with "qBittorrent may still own downloads in this folder. Restore its connection before retrying; … a second torrent engine was not started." The seeder sends nothing and no native partial appears. Once qBittorrent is back, Retry completes the season through it. | Pass. The first run failed on a test bug: the stand-in reported an empty save path, and the app correctly refused it as another folder. The stand-in now reports the save path the app posted, as qBittorrent does. |

## Proposed exclusions, for the user to approve

- **`T2_Downloads_03_Disk_Space_Exhaustion` and physical disk-full or low-reserve acceptance.**
  - A real full disk needs a small volume mounted by an administrator. The user declined to mount one on October 23, and the shared system drive is never filled.
  - The placeholder stays skipped with this reason and is never counted as a pass.
  - Existing evidence:
    - Simulated initial, sidecar and falling capacity on both native paths ([capacity QA](download-capacity-stale-handoff-2026-10-06.md), [season space QA](permanent-season-space-2026-10-08.md)).
    - The second-monitor falling-reserve journey with visible Retry.
    - Real NTFS write-ACL denial ([ACL QA](native-torrent-acl-denial-2026-10-21.md)) and real OS file-sharing failures ([file-access QA](native-file-access-failures-2026-10-08.md)) on both native paths. Both use the same typed write failure a full disk would raise: "Cannot write download files. Check the destination's permissions and free space…".
  - By design the native engine refuses to start or continue below a 1 GiB reserve, so a real ENOSPC would need another process to fill the volume between the 3-second reserve checks.
- **Mounted-folder and network volumes.** Mounting a volume at a folder, or creating an SMB share, also needs administrator rights, so neither was run. By the existing design, a location whose physical path cannot be named stops with "The download folder's physical location could not be verified…" and keeps its files ([DOS alias QA](torrent-dos-drive-alias-2026-10-16.md)). One inference is not verified: a volume mounted only at a folder, with no drive letter, may always be refused this way, because the final-path query asks for a DOS name.

## Still open

- **Real qBittorrent path handling.** qBittorrent is not installed on this machine (none in either Program Files folder, the per-user Programs folder or WinGet). Qualifying the real client needs qBittorrent (a portable copy is enough) with its WebUI on a loopback port and an isolated profile. The app no longer trusts its reported names either way.
- **Long or many-peer real-internet transfers.** These are not exercised; every case here is loopback.

## Runs

TRX files are kept in `C:/Users/user/animeapp/.artifacts/implementation/download-storage-20261023`.

| Run | Result | TRX |
| --- | --- | --- |
| Refused-torrent regressions, before | 0 / 4 | `refused-torrent-before-20261023.trx` |
| Refused-torrent regressions, after | 4 / 4 | `refused-torrent-after-20261023.trx` |
| Containment, validation and refusal neighbours | 16 / 16 | `refused-torrent-related-20261023.trx` |
| Malformed magnet, before / after | 0 / 6, then 6 / 6 | `malformed-magnet-before-20261023.trx`, `malformed-magnet-after-20261023.trx` |
| Download cohort after both batches (pinning, qBit ownership, feed failures, season validation, native boundary, containment) | 82 / 82 in 1 min 48 s | `batch1-2-related-20261023.trx` |
| Season audio, before / after | 3 / 6, then 6 / 6 | `season-audio-before-20261023.trx`, `season-audio-after-20261023.trx` |
| Season and temporary-watch cohort after the audio change | 120 / 120 | `season-audio-related-20261023.trx` |
| Real-app traversal, malformed magnet and audio journeys | 3 / 3 in 38 s | `downloads-ui-first-20261023.trx` |
| Alias transfers and retargeting | 4 / 4 in 1 min 31 s | `alias-transfer-first-20261023.trx` |
| qBittorrent transfer in another folder, before / after | 0 / 2, then 2 / 2 | `qbit-foreign-folder-before-20261023.trx`, `qbit-foreign-folder-after-20261023.trx` |
| qBit, pinning, validation and feed classes after that | 59 / 59 | `qbit-foreign-folder-related-20261023.trx` |
| Real-app qBittorrent offline journey | First run 0 / 1 (test bug above), then 1 / 1 in 28 s | `qbit-offline-first-20261023.trx`, `qbit-offline-second-20261024.trx` |
| Whole Downloads class, after the shared fixture changes | 9 / 9 passed, 1 skipped (`T2_Downloads_03`, the proposed exclusion), in 2 min 15 s | `downloads-class-20261024.trx` |

All runs used one Debug build at `.artifacts/implementation/build`, with zero warnings and SDK 9.0.310. The hosted run remains the full checkpoint. Skipped Downloads methods drop from **five to one** (`T2_Downloads_03`, the proposed exclusion).

## November 10: Delete, and the merged code

Rechecked on the merged `codex/app-recovery` at **401155b**, where all ten session branches are combined.

**Delete left a download's captions and saved details behind.** download-captions found this on Sintel and Breaking Bad: Downloads → Delete removed only the media file. A saved Movie/TV item's caption files and `library-item.json` stayed in its folder, and a season-torrent episode's sidecar captions stayed beside the deleted episode. Three regressions in `SeasonLibraryDownloadTests` failed on the merged code (`delete-before-20261110.trx`):

| Case | Before | After |
| --- | --- | --- |
| Two saved episodes in one season folder, plus a user file there; delete episode 1 | Its caption and `library-item.json` were left behind | Its media, caption and record are gone, and its own revision folder is removed once empty. Episode 2's files and the user's file are kept. |
| Season-torrent episodes sharing a folder; delete `Show - 01.mkv` | `Show - 01.srt`, `Show - 01.en.srt` and `Show - 01.English.forced.ass` were left behind | They are gone. `Show - 01.5.*`, `Show - 02.*` and `Show - 01 notes.txt` are kept: a sidecar belongs to the file whose name is its longest prefix. |
| A temporary watch still owns a file | Deleting a library copy already left the watch's own file alone, but a file inside a temporary watch folder (listed when the download folder contains it) was deleted | That file is refused and kept: "This file belongs to a temporary watch. Close its player tab; the app removes it then." |

Delete removes the media first, so a locked or protected file stops it before anything else is removed. If an item's saved record cannot be read, only the media and its sidecars are removed; its listed captions are not guessed.

| Run on the merged code | Result | TRX |
| --- | --- | --- |
| Delete regressions, before / after | 0 / 3, then 3 / 3 | `delete-before-20261110.trx`, `delete-after-20261110.trx` |
| Library, readiness and playlist classes | 98 / 98 | `delete-related-20261110.trx` |
| My earlier download cohort (pinning, qBit ownership, feeds, validation, native boundary, containment, alias transfers, SUBST) | 97 / 97 in 3 min 18 s | `merged-download-cohort-20261110.trx` |
| Whole Downloads class in the real app | 9 / 9 passed, 1 skipped (`T2_Downloads_03`), in 2 min 34 s | `merged-downloads-class-20261110.trx` |

The merge broke nothing in this item. The November TRX files are kept in `C:/Users/user/animeapp/.artifacts/implementation/download-storage-20261110`. The two exclusions above remain proposals until the user confirms them in this session. Real qBittorrent path handling stays open because qBittorrent is not installed.
