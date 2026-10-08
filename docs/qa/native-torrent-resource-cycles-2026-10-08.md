# Native torrent pause/resume resource cycles — October 8, 2026

Status: **both reproduced faults repaired; scoped real-transfer/resource qualification passed**. This is a private localhost transfer through the production queue and Downloads commands, with an isolated profile. It does not qualify real-provider identity/audio, external qBittorrent or packaged acceptance.

## Baseline

The existing 30,416,538-byte generated seed was reused through NTFS links, without another media copy. The private local seeder and ordinary Release build were reused. Twenty actual pause/resume cycles used 20 distinct native managers/engines; each received further real pieces and stopped, retained its partial, and left the one permanent Ready video unchanged. Last manager progress was **1.1308562197092082%** and the final stop took **134.4927 ms**. The actual second-monitor held capture at **11:39:08.507–08.871 UTC**, with exact PID/path/start-time verification, showed **Paused / 0%**. The rapid cycle finished before the three-second progress callback and the start callback reset the job to zero. This progress defect is not a pass.

After 60 seconds of natural settling, private memory was **200,216,576 bytes**, handles **1,732** and threads **23**. Six separate diagnostic Gen2 collections left **0/20 downloaders**, **20/20 managers** and **20/20 engines** alive; private memory was **200,622,080 bytes**, handles **1,729**, threads **24**. These forced collections are diagnostic, not natural-memory acceptance. Root tracing reported a strong path for **one engine/manager pair** through the MonoTorrent DHT timer/event callback to ClientEngine and its registered torrents. The other 38 target objects had no reported path; this is not proof that all 20 pairs share that root or are permanently leaked.

Ready and partial hashes were unchanged during the settled pause. Ready SHA-256: **EE92E583D073F7A6035A0C1757D55C6247061321034C3F7D1771B0E5B80F81D8**; partial: **9DC39190858A1CF9BFAFEA16DE51BAC02BAB959602237CEA0A9BF1E2A05C289E**. The private probe shut down normally at **11:45:56.925 UTC**. Its result marker covers command/partial/Ready workload assertions only; it does not assert truthful displayed progress or collection.

Small evidence is under `.artifacts/implementation/torrent-resource-cycles-20261008/observed-20-cycles`. No original profile or user media was used or modified.

## Next

Qualify temporary native torrent watch cancellation/cache/final-owner resources next. Broader provider, external qBittorrent, sustained and packaged gates stay open.

## Repair and focused verification

`SeasonDownloader` now reports stopped-manager piece progress before validation weighting and unregisters the stopped manager with **KeepAllData**. Its final supported DHT-settings transition detaches callbacks before engine disposal, including failures before manager creation; active transfers still use DHT discovery. Nested cleanup preserves unregister/disposal if a progress callback throws. The [pinned MonoTorrent 3.0.2 implementation](https://raw.githubusercontent.com/alanmcgovern/monotorrent/release-v3.0.2/src/MonoTorrent.Client/MonoTorrent.Client/ClientEngine.cs) supports that transition; its ordinary Dispose does not detach these handlers.

Both valid real-transfer regression cases failed before repair: paused persisted progress stayed zero, and the closed manager stayed registered. The initial too-short fixture could complete before pause and is excluded; the final 90-second fixture explicitly waits for Downloading. After repair both cases passed, including another resume/pause, preserved fast-resume/metadata/partials and exact persisted job identity/progress. The **58-case related queue/native-temp/library cohort passed, zero failures/skips, in 32 seconds** with a zero-warning/error Release build. Repaired actual-window and ordinary-resource qualification is underway.

The repaired held second-monitor capture at **11:57:23.280–23.547 UTC** showed **Paused / 1%**, matching weighted retained piece progress **1.1766289714593428%**. Resume/Cancel were enabled and Pause/Retry disabled; only the unchanged permanent Ready video had Play. This qualifies the rendered command-driven paused state, not human button interaction. Natural/resource settling and an ordinary process without desktop queries are running.

## Repaired resource results

Both repaired workloads completed 20 distinct sessions with truthful final piece progress, no active downloader during settled pause, retained partial/cache hashes and the unchanged Ready file. The observed sample saved weighted job progress **1.1766289714593428%**; the ordinary sample saved **1.0743134087237478%**. All **40/40 downloader, manager and engine triples** were collected after separate fixed diagnostic rounds. Each exact owned heap inspection found **zero matching SeasonDownloader/TorrentManager/ClientEngine objects**. The ordinary workload made no desktop queries.

| Sample | Natural 60-second private bytes / handles / threads | After diagnostic collection | Remaining downloader/manager/engine |
| --- | --- | --- | --- |
| Observed | 206,127,104 / 1,762 / 30 | 198,299,648 / 1,743 / 30 | 0 / 0 / 0 |
| Ordinary | 206,233,600 / 1,778 / 31 | 199,032,832 / 1,751 / 31 | 0 / 0 / 0 |

This proves finite closed-session collectibility, not return to startup memory or overnight stability. MonoTorrent’s retired DHT timers may themselves live until their library timeout; active discovery remains enabled. Both owned apps exited normally at **11:59:44 UTC**, and the owned seed stopped normally; all three exact processes were absent afterward. No existing user app was closed.

Tested Release Core SHA-256: **38767464EF58D5B573D1CC2EB882DCB62E61A487D1E1EC64256C2B942A9D9C0D**; WPF: **65009B67CF5311723F1847A9BE168567F9470CFD8D17DDE71BFD44CB6B2F70D6**. No production WPF/Python source changed in this native batch. The small helper reused the ordinary Release directory and existing generated media. Both source regressions and the 58-case cohort passed; full hosted acceptance of this new source remains pending publication.

Read-only preservation again confirmed all six original videos (**2,028,915,092 bytes**), original configuration/metadata/captions/mtime snapshots and all five SQLite table contents unchanged. Database content SHA-256 remains **e3fd7e97b1eeb4b4dff8d38f09b04a4a8ad19dafd2a835fd1449b006778328b1**, with the same eight independent resume rows. Unrelated untracked files remain untouched.
