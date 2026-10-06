# Cache-backed legacy partial readiness — October 6, 2026

An older native torrent could leave an unfinished video with its ordinary `.mkv` extension. Downloads offered Play even while its queue was Paused. The later `.!mt` repair prevents this for new/resumed transfers, but did not protect a legacy file before its first Resume.

## Repair

Downloads now performs a bounded, read-only check of the native client's existing torrent/resume cache during its background scan. It verifies the metadata info hash against the cache filename and resume record, confines metadata paths to the configured download root, and checks actual piece bytes. Recognition requires both a matching completed piece and a differing/missing piece marked incomplete. A stale resume bitmap alone cannot hide a complete file, and title similarity never establishes ownership.

Recognized partials are excluded from ready playback; a stale Play action checks again and reports that the partial must resume before playing. Published app library entries retain their existing canonical routing. This does not rename, delete, repair or resume any file or queue job. Books retain their existing routing.

Checks are limited to 256 metadata entries, two MiB per cache file, 32 MiB total metadata/piece reads, eight piece probes per media file and piece sizes up to four MiB. Unsupported/unreadable cache, pure-v2 metadata, files consisting only of shared boundary pieces, no matching completed piece and exhausted probe budgets remain unknown and unchanged. Existing `.!mt` handling remains the normal readiness mechanism for current transfers. External qBittorrent caches are not qualified here.

## Verification

- The scan/stale-action regression reproduced the old visible partial. An initial physical check additionally exposed a truncated partial smaller than its declared torrent length; a new case reproduced that miss. The final repair recognizes absent bytes beyond EOF while retaining the completed-piece requirement.
- **67 focused checks passed**, zero failures/skips, including **12 new cases** and existing queue, routing and permanent-library/season checks. They cover sparse and truncated partials, multi-file paths, complete bytes with stale resume, unrelated bytes, wrong hashes, missing/corrupt cache, invalid bitfields, unsafe paths, stale Play rejection and a published library entry with conflicting old cache. Byte hashes and write times remain unchanged. [Results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/legacy-partial-final-2026-10-06.trx).
- Actual second-monitor Release QA reused the queue profile. An additional NTFS hard link exposed its retained paused partial under the old unsuffixed filename, without copying or altering the payload. The real cache had **78/1857 completed pieces**. Before the truncated-file repair, that card remained visible; afterward it was absent and the queue remained Paused at **3.785675821%**. The completed fixture and permanent Pilot/episode cards remained visible. Explicit Play of the completed fixture rendered native frames; K paused at **46.220 seconds**. No queue action was invoked.
- All original partial/metadata/resume-cache hashes and write times matched before/after. Only the added QA hard link was then removed after checking its resolved path and shared file identity; the original `.!mt` partial remains. Small private evidence is in `.artifacts/implementation/native-short-completion-20261006/`.
- Final Release build: zero warnings/errors. WPF SHA-256 `972E34F4D075F91EE831C3A9DB81FC327701AF4A42C803DF3580998E67EF6AE5`; Core `011F380B15CE643E9B47FB0D59AB6ED3B0E73FC31602543AFB3ABAD1C0EC6AB2`. Python source is unchanged; its latest full result remains 178 passes. The latest full local C# result remains the preceding localization batch's **885 passes/22 skips**; these 67 checks are scoped.

## Preservation and remaining gates

Read-only preservation at **00:47:46 UTC** verified all six protected permanent videos (**2,028,915,092 bytes**), metadata/captions, original configuration and separate film/TV resume rows. Only the explicitly played generated fixture's isolated position advanced. The human's existing Debug process was left running; Books/imports were not opened or changed. Existing build outputs were reused.

The short-local completion/replay batch is committed as `d364837`; its authorized push failed connecting to GitHub. Hosted status for the preceding localization run remains unconfirmed after API connection failures. Broader download ownership/failures, spoken audio, providers, full seasons, native caption dropdown, manual higher Windows DPI and packaged release remain open. Books stay deferred, with Arabic cartoons last among non-Books work.
