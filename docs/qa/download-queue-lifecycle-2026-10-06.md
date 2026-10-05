# Download queue shutdown, restart and partial-file readiness

Date: October 6, 2026 (Asia/Amman). Scoped recovery evidence; broader download/player/release acceptance remains open.

## Reproduced problems and repairs

Closing the isolated Release app during a real native torrent stopped the transfer and saved Paused at **41.335%**, but the app did not exit within **12 seconds**. Only that hung QA process was subsequently terminated. The queue's synchronous WPF shutdown waited on an asynchronous pause continuation that could capture the blocked dispatcher. An unchanged regression reproduced that deadlock before repair.

`DownloadQueueService.Dispose` now starts the entire pause chain on a worker and bounds the wait, including an executor that ignores its cancellation token. It retains the existing five-second stop deadline, subsequent bounded worker drain and saved queue. The deadline bounds waiting; it cannot force an uncooperative external torrent client to acknowledge a stop.

The old native download also left a preallocated, incomplete `.mkv` listed with **Play**. `SeasonDownloader` now enables MonoTorrent's unfinished `.!mt` filenames. Actual Resume renamed the existing partial in place and reused its pieces; Refresh excluded it from ready media. Completion removed the suffix only after the transfer finished. A legacy unsuffixed partial can still appear before its first native Resume; this batch does not migrate every old partial on idle startup.

## Actual second-monitor Release journey

An isolated copied database/config and six existing permanent videos were used. Videos were NTFS hard links, avoiding another 2 GB copy. A bounded localhost RSS/tracker/seeder supplied a generated **90-second silent video**, with the existing unavailable-qBittorrent fallback exercising production MonoTorrent. Two controlled jobs were prepared as Paused; actual Downloads Resume/Pause/Cancel/Retry buttons drove the production queue. This does not qualify real catalog selection, season-button enqueueing, item identity or spoken audio.

| Actual check | Result |
| --- | --- |
| Restart after old shutdown | Same job ID, Paused 41.335%; partial/cache unchanged while idle; no automatic transfer. Explicit Resume progressed to 48.702%, then Pause retained the data. |
| Fixed close during active native transfer | Normal app Close exited and saved Paused **71.826%**, retained `.!mt` data/cache. A subsequent restart stayed paused and hid the partial from ready files. Separate paused closes exited in approximately 0.08–0.10 seconds; these are samples, not a shutdown benchmark. |
| Native completion | First fixture reached Completed 100%, production probe validation passed and the final 30,416,538-byte file matched the seed SHA-256. The existing completion handler opened it in the native player and rendered frames. |
| Partial cancellation | A second 32 KiB/s fixture was cancelled at **1.944%**. The app explicitly confirmed partial retention and showed Cancelled. Across **10.8 seconds**, file/cache hashes and modification times and the seeder upload count stayed unchanged. |
| RSS outage and malformed magnet | Actual Retry against two controlled unavailable feeds, then against an invalid `btih`, settled Failed. Both retained the identical partial/cache and transferred no additional data. Restoring the feed and Retry reused the partial, progressed above 3%, then Pause retained **3.786%**. |

The first fixture completed before the attempted Cancel reached it; that attempt is **not** cancellation evidence. The second fixture supplies the actual partial-cancellation proof. Both generated files and the paused second job remain in the isolated profile. Only owned QA apps/seeders were closed.

Completed file SHA-256: `EE92E583D073F7A6035A0C1757D55C6247061321034C3F7D1771B0E5B80F81D8`.

The completed 90-second local clip subsequently reached its final frame but showed “ended before video playback began.” The existing completion credibility guard rejects durations below 120 seconds to avoid counting adverts. This remains an open distinction between short legitimate local media and unverified provider media; no matching/completion guard was weakened. Native frames do not establish error-free full playback of this fixture.

## Caption report and verification

The user confirmed **“Yes, the CC button works.”** This closes the direct-button question only. Their earlier dropdown report (“goes back to english instantly”) remains unresolved. Six generated playing/paused mouse cases, including restored-window dropdowns, passed separately. The desktop helper's main-owner activation can dismiss a related popup before its item click, so that rejected interaction does not establish a caption-engine failure. No new production caption change was made in this batch.

The shutdown reproduction failed before repair. All **seven queue tests** passed after the final repair, including captured-dispatcher and cancellation-ignoring executor cases. The final full Release suite passed **868 tests, zero failures and the same 22 skips**, 890 total, in **7 minutes 16 seconds**. It includes the final queue repairs and all six caption mouse cases. Build: zero warnings/errors. Python production code is unchanged; its latest full result remains **178 passes**. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/download-lifecycle-full-2026-10-06.trx), [queue regression](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/queue-bounded-shutdown-final-2026-10-06.trx), [caption mouse check](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/caption-restored-human-mouse-2026-10-05.trx).

Tested Release WPF SHA-256: `C703241AD12EDB0AE8E3398CC7B3E5D7483F4FA01FB43B7CBA47E032D7B82490`; Core: `D357FBA4BA4AA7366F27988674E8BAB0E3CE686A88115E2608A9B80B882DA1E2`.

## Preservation and limits

The final read-only preservation probe retained all **six originals, 2,028,915,092 bytes**, their modification times, metadata/caption hashes and original config. Original positions stayed separate: Pilot **803.205 s**, S1E2 **132.484 s**, S2E1 **371.022 s**, Dune **318.291 s**. Alice/imports and the user's existing Debug app were untouched. No cleanup/reset or new full build copy was performed; existing build locations were reused.

Private snapshots and screenshots remain under `.artifacts/implementation/download-queue-lifecycle-20261005`. A Windows Security prompt previously obstructed the first session; no security controls were automated. The later session proceeded after observing the app unobstructed. Disk limits, temporary-watch stale handoffs, external qBittorrent, real provider failures/audio, legacy-partial migration, broad DPI/RTL and packaged release checks remain open. Books remain deferred; Arabic cartoons remain last among non-Books work. The user now permits the larger main monitor when helpful.
