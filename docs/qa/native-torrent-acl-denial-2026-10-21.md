# Native torrent write-permission (ACL) denial — October 21, 2026

Part of the open "live provider/storage failures" gate, which listed native torrent ACL denial as unqualified. Branch **codex/app-recovery** on **8081f94**. Only generated localhost torrents and temporary fixture folders were used. Real NTFS permission entries were applied only to fixture partials and removed in `finally`; no user data, profile or disk capacity was touched.

## Method

Both cases reuse the existing real local seeder from `NativeTemporaryDownloadBoundaryTests`: a generated multi-megabyte video served by a loopback tracker and peer. Each adds an NTFS **deny** entry for the current user covering write and append data on the partial file. The partial stays readable, deletable and re-permissible, and a direct write attempt fails with `UnauthorizedAccessException`.

- **Permanent season download** (`PermanentSeasonWriteAclDenialEndsPromptlyAndRetainsRetryablePieces`).
  - The job downloads real pieces, is paused, the deny entry is applied, then the job is resumed.
  - It promptly reached **Failed**, retryable, with "Cannot write download files. Check the destination's permissions and free space, and close other apps using these files."
  - The partial's SHA-256 was unchanged. After the entry was removed, **Retry** resumed the same job with new pieces, and the permanent sentinel file was retained.
- **Temporary episode watch** (`TemporaryEpisodeWriteAclDenialStopsProviderRotationAndCleansOnRestart`).
  - The deny entry is applied to the partial as the engine starts the episode.
  - The watch returned the typed local write failure without rotating to the other provider candidate, and disposed its engine.
  - Unlike a held sharing lock, a deny entry does not block deletion, so the owned job folder was removed promptly. Restart cleanup left the temporary root empty and the permanent sentinel intact.

The existing SDK error-state handling, added October 8 for sharing locks, already covers ACL denial on both paths; **no production change was needed**.

## Runs

| Run | Result | TRX |
| --- | --- | --- |
| Permanent case, first version | 1 / 1 (5 s) | `native-torrent-acl-denial-first-20261021.trx` |
| Both cases; the permanent case tightened to require the write message | permanent passed; temporary **failed in the test's own cleanup**. Removing the deny entry found no file, because production cleanup had already removed the owned job. The cleanup now tolerates that. | `native-torrent-acl-denial-20261021.trx` |
| Temporary case after that test fix | 1 / 1 (5 s) | `native-torrent-acl-denial-temporary-20261021.trx` |

## Limits

- Only a file-level write denial on an existing partial is covered. Folder-creation denial, read denial and denial on a library destination that must be created are not.
- A physical disk-full or low-reserve volume still needs a real small volume, which requires administrator rights to mount; the user can provide one later. Simulated capacity failures are already covered.
- Real internet torrents, mounted, network or removable destinations, and older external transfers stay open.
