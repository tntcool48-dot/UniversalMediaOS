# Movie/TV original item evidence — October 1, 2026

The scraper now retains the provider's original decoded item response and binds it to the active native stream. A failed second metadata lookup no longer removes otherwise available identity evidence for this player. Final production progressive/native-player checks passed for Dune (2021), Breaking Bad S1E1 and S2E1. These are scoped service/player checks, not broad provider or physical UI acceptance.

## Problem and repair

The old enrichment path fetched item metadata again after the provider had already decoded its stream response and started the player. That second lookup had a two-second limit. If it failed, the app could return playable media without independent identity or the bound in-memory captions. The preceding progressive Dune failure lacked identity; it did not record the precise reason for that failed metadata observation. This batch reproduced the failure mechanism by making only the redundant enrichment request unavailable, rather than attributing an unobserved HTTP status to the earlier run.

The existing provider decoder returns both item metadata and decoded stream URLs. A small response observer now copies only its bounded item fields and default subtitle descriptors while preserving the original function context, promise, value and errors. Retention is limited to four responses of at most 256 KiB each, with at most 24 stream URLs and 12 default subtitle descriptors. Copies stay in the owned browser document and disappear with it; no library-data migration or persisted metadata cache was added.

Cross-site player frames run in separate renderer targets. A root-document script alone missed their initial response in the first fault probe. The final setup enables the page domain and installs observation before each owned page/frame runs, recursively covering nested targets. Every attached target is resumed even when installation fails. This uses the browser's existing debugging connection and leaves the metadata fallback available. DrissionPage 4.1.1.4 was the tested runtime. The [Chrome Target protocol](https://chromedevtools.github.io/devtools-protocol/tot/Target/) describes the target attachment mechanism.

Enrichment accepts an observed response only when its API address matches the active player's API, its release filename matches the active item, and both its returned stream list and the active player's stream list bind the current media URL. Existing content-bearing query checks, independent IMDb/title/year/form/unit checks and caption timing/size checks remain in force. Known wrong movie IDs and episodes still reject the native candidate. Request CONFIG does not establish identity, captions do not establish audio, and unknown evidence remains unknown. Unsupported/missing observations retain the previous metadata lookup; the existing 34-second operation and three-second caption budgets were unchanged.

## Verification

- **156 Python tests passed**, including nine new cases covering original-response handoff without refetch, wrong API/release/media/identity/unit, actual JavaScript promise/context/error behavior, bounded immutable response retention, and frame initialization/resumption after failure.
- **580 C# tests passed, zero failures, 22 existing skips, 602 total**, in 1 minute 42 seconds. No C# production source changed in this batch. [Full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/movie-tv-item-readiness-2026-10-01.trx), [Python results](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-item-readiness-20261001/python.log).
- WPF/test build: zero warnings/errors. Source, WPF-output and test-output audiovisual script SHA-256 all match: `31E44E87FD96D7DD83D14121F47F0BEAC70820DFFF9BAA7D8A0A2272A54DCF80`. WPF DLL remains `E5FF2C20A210BF34B36D538E736C04DB988DAD7DC21F3875AADDF2A60B777AD2`; Core DLL remains `C9B074764EC79418A2A786961B1AACC45EFB6F09C422A8F94A29B9673EA9A2CA`.

The live metadata-outage probe initially returned an unverified native result when the extra lookup was blocked and original frame observation was missing. With final frame initialization, the same fault guard returned independently matched Dune identity and English captions in **18.813 seconds** using the original response; no metadata-refetch call or blocked-refetch attempt occurred. Eight target initializations completed without protocol errors. [Fault probe](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-item-readiness-20261001/enabled-metadata-blocked.log). This is a controlled failure probe, not a claim that a live provider naturally returned an outage during that run.

The final normal helper used production bootstrapper, progressive resolver, exact matcher, loopback proxy and `PlaybackViewModel` with isolated profiles and muted memory-output video. It opened no main-monitor window.

| Requested item | First native update / handoff | Loaded caption bytes | Paused / resumed position |
| --- | --- | --- | --- |
| Dune (2021) | 23.261 / 23.263 seconds | 83,083 | 81,906 / 82,508 ms |
| Breaking Bad S1E1 | 18.447 / 18.647 seconds | 40,779 | 137,702 / 138,305 ms |
| Breaking Bad S2E1 | 19.153 / 19.373 seconds | 31,757 | 103,498 / 104,098 ms |

All three independently verified the requested film/unit, decoded native video, selected English captions, sought into a timed cue, paused inactive, stayed paused on return and resumed on command. Dune and TV frame captures visibly show English captions. Each retained the unknown-audio notice because spoken-language tags were absent. [Normal progressive run](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-item-readiness-20261001/native-progressive.log), [sanitized results](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-item-readiness-20261001/loaded-native-results.json).

These network-dependent times do not prove a general speed or accuracy rate. The earlier missing-identity run remains dated evidence; the new fault test establishes this specific repair and the final samples establish current scoped watchability. Other providers, film cuts/spoken audio, full-duration captions, provider-change/restart resume, special mapping, DASH validation and HLS/DASH/season downloads remain open. Physical catalog-to-player/fullscreen journeys on the secondary monitor remain unvalidated after the earlier desktop-tool runtime failure. The memory-output harness retains its existing VLC converter/on-top/subtitle-option warnings, which were not repaired in this batch.
