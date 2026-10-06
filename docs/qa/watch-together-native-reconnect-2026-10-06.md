# Native Watch Together reconnect — October 6, 2026

A controlled pair of production Watch Together clients, the real local WebSocket relay, and two production native player viewmodels reproduced unintended playback while applying an already-paused host state. `native-watch-reconnect-before-2026-10-06.trx` failed: **one failure, zero passes**, at the first reconnect, with one unintended native Playing event. Both inputs had decoded the generated local video and were paused before connection; this was not a selected-label assertion.

The native remote pause/buffering-pause branch now sets the requested paused state with `SetPause(true)` and retains `_pauseWhenStarted`. Repeating a pause no longer toggles native playback. Explicit play still clears the pause request. No provider matching, caption selection, tab ownership or persisted identity logic changed.

The first repaired case passed in eight seconds. The final cohort passed **101 checks**, zero failures/skips, in **56 seconds**, with a clean reused Release build. [Final results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/native-watch-reconnect-confirmed-2026-10-06.trx). It covers native completion/delayed resume, subtitle-independent player ownership, cleanup, hardening, resume contention and existing Watch Together relay/order guards.

The new case reconnects the peer **20 times** in the same room while both native decoders are paused; each new initial synchronization must retain pause, exact observed host time plus a 2.5-second peer offset, roles and host connection. It observes **zero unintended native Playing events**, ending at host/peer **65,000 / 67,500 ms**. Repeated buffering-pause messages also retain pause. The host then disconnects, the peer is promoted, the former host rejoins as peer, and actual native slider-seek/play/pause commands propagate with the offset to both decoders. Both produced video frames and retained error-free pause at the end. Accepted persistence drains before either test storage directory or connection pool is removed.

Resource samples after reconnects 5/10/15/20, with explicit managed collection:

| Reconnect | Managed heap bytes | Private memory bytes | Process handles |
| --- | ---: | ---: | ---: |
| 5 | 8,156,912 | 188,526,592 | 1,249 |
| 10 | 8,276,496 | 188,444,672 | 1,253 |
| 15 | 11,531,480 | 188,469,248 | 1,254 |
| 20 | 8,232,264 | 185,782,272 | 1,254 |

These samples show settling within this two-player reconnect case. They do not certify all application processes, transfers, providers, image cycles or overnight behavior. The generated video has no spoken audio. Native video uses muted memory outputs, so physical two-window UI, real remote networks, displayed native captions and packaged acceptance remain unqualified. Books remain deferred and Arabic cartoons last among non-Books work.

Tested source: `59944f1` plus this native pause/test follow-up. WPF SHA-256: `51841EA6119F948BDE5F9110B01E777385DECE07233E4D838354AEA54F0D69C6`; Core SHA-256: `6BE31330BACEEAD878A6D6B654995B658B5AF4BC9F2BC6F7A2E9DF41C02C6858`. The preceding queued-read/four-failure [full hosted run](https://github.com/tntcool48-dot/UniversalMediaOS/actions/runs/37505442598) is in progress; it does not include this new native pause repair. No new full passing checkpoint is claimed here.
