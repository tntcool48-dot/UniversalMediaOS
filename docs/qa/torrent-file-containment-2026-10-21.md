# Torrent file entries outside the download folder — October 21, 2026

Addresses the torrent part of the gap recorded for the skipped `T2_Downloads_01_Output_Path_Traversal_Attempt` placeholder in the [skipped-test inventory](skipped-test-inventory-2026-10-05.md). Branch **codex/app-recovery** after **07d0eca**. Only generated localhost torrents and temporary fixture folders were used, and every entry a test names lies inside that test's own temporary root. No profile, library, download or app window was involved.

## Change

`TorrentFileContainment.FindOutside` resolves each file entry's full, complete and partial (`.!mt`) paths with `Path.GetFullPath`, as Windows resolves them on open. It returns the first path that is not strictly inside the job's folder. Both native paths call it on an unstarted manager, before anything opens a file:

- **Temporary episode watch:** right after `engine.AddAsync(torrent, directory)`, before identity, priorities, space and `StartAsync`. It logs "Torrent lists a file outside its download folder; trying another result." and tries the next result.
- **Permanent season:** in both metadata branches, before the space estimate (which already looks at every file) and `StartAsync`. It fails with "The torrent lists a file outside the download folder; it was not started.", logged as a `[MonoTorrent] Error:`. qBittorrent is still tried first, unchanged.

## What MonoTorrent 3.0.2 lets through

A throwaway probe (not committed) added single entries to an engine without starting it:

| Entry | MonoTorrent 3.0.2 | Check |
| --- | --- | --- |
| `..` segments, `C:\…`, `C:x`, `/…` | rejected when loading | — |
| `\Users\…`, rooted at the current drive | accepted; resolves to `C:\Users\…` | refused |
| `\\host\share\…`, `\\?\UNC\…` | accepted unchanged | refused |
| `\\?\C:\…`, `\\.\C:\…`, `\??\C:\…` | accepted unchanged | refused |
| `NUL` | accepted; .NET resolves `…\NUL` to `\\.\NUL` | refused |
| `name:stream`, `.. `, `...`, `COM1.txt` | `:` becomes `_3a_`; the rest stay inside on this Windows 11 build | accepted |

`TorrentFileContainmentTests` now also pins the current-drive (`root-relative`) and `unc` forms. The class comment no longer claims MonoTorrent rejects rooted names.

## Why the two end-to-end cases failed

The handed-over cases failed only on `Assert.False(Directory.Exists(fixture.Outside))`. The **test-side seeder** created that folder before the app ran. Its own manager had the generated video and subtitle on disk, and its start-up hash check (Hashing → Downloading) created a 0-byte `outside\escape.bin`:

| Seeder | Outside entry created |
| --- | --- |
| Every file wanted (as handed over) | yes, during hashing |
| Only the outside entry unwanted | yes: it shares the first, wanted piece |
| Every file wanted, empty save folder | no |
| No file wanted (the fix) | no |

The fixture's source files were unchanged in every variant. The outside-entry seeder now marks every file DoNotDownload before starting, so it only serves metadata. It also asserts that the outside folder is still absent once it reaches Downloading. This failure needed no production change.

## Mutation checks (not committed)

- **Both checks disabled, empty folders:** both cases still failed, but not at the folder assertion. The temporary watch ran until the 20-second cancellation (`TaskCanceledException`). The season logged "Starting download". With no file of the torrent on disk, no hash check created anything outside.
- **Season check disabled, folder holding a file:** the season case now first writes one of the torrent's files (`library\source\… .en.srt`), as a resumed season would hold. The app's own engine then created `outside\escape.bin.!mt` (0 bytes) outside the download folder, renamed the existing subtitle to `.!mt` and created the video's `.!mt`. The case failed at the folder assertion. With the check, the outside folder never exists and the subtitle is unchanged.
- **Temporary watch:** its job folder is always new, so its folder assertion is only a sanity check. A missing check fails it through the exception type and the "Downloading only episode" log.

## Runs

| Run | Result | TRX |
| --- | --- | --- |
| As handed over | 4 / 6; both end-to-end cases failed at the folder assertion | — |
| Final focused: 6 theory rows and both end-to-end cases | **8 / 8** in 7 s | `torrent-file-containment-focused-20261021.trx` |
| Related: the native boundary, containment, temporary-watch, special-episode, season validation/feed and qBit ownership classes | **165 / 165**, no skips, in 1.8 min | `torrent-file-containment-related-20261021.trx` |

Debug build with 0 warnings and SDK 9.0.310. The hosted run remains the full checkpoint.

## Limits

- qBittorrent places files itself; its path handling is not tested here. The UI placeholder stays skipped because no visible Downloads outcome is tested.
- This is a path check. Links inside the download folder are covered by the [payload-link checks](download-release-pinning-and-payload-links-2026-10-16.md).
- DOS device names follow Windows' path rules: `NUL` is refused, while `COM1.txt` stays an ordinary name on this build.
- In the mutation, MonoTorrent's partial-file mode renamed an existing season file that failed its hash check. That behaviour is outside this change.
