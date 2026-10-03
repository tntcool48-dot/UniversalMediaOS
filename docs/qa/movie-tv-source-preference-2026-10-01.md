# Movie/TV source preference — October 1, 2026

The source coordinator now keeps searching after a playable but independently unverified native result. A later native result with independent film/episode evidence takes priority in both the collected source list and progressive catalog list. This fixes premature cancellation; it does not establish broad provider reliability or final Movie/TV acceptance.

## Cause and change

Previously, `MediaValidated` caused the first HTTP(S) native candidate to cancel the other extractor processes. Byte validation establishes that media can play, not which film, remake, season or episode it contains. The preceding combined Dune probe exposed this gap.

The scraper now publishes each usable candidate through the existing source-update pipeline. Only a native result with independently verified identity/unit can stop its alternatives. Full request verification still rejects observed title/year/unit/audio conflicts before that decision; missing language evidence stays unverified. Request-echo metadata cannot win. The browser concurrency limit remains two, all queued alternatives remain eligible until a confirmed success, and no new extraction timeout was introduced. Iterator disposal and cancellation stop owned extraction work.

The catalog inserts confirmed native identity/unit matches ahead of earlier unknown native sources, while keeping website opening separate. If the same URL later carries better evidence, it replaces the earlier card instead of being discarded as a duplicate. Audio notices and verification labels remain honest; English captions do not establish spoken language. The list API uses the same preference.

## Tests and build

- Ten added C# cases exercise unknown/request-echo candidates before a held independent match, collected-list ordering, extractor disposal, known episode/audio conflicts, progressive publication and failure retention, and catalog ordering/upgrades for identical and different URLs. The existing fast-success cancellation case now requires independent film evidence while preserving missing-audio notices.
- **89 focused cases passed**, zero failures/skips: [focused results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/movie-tv-source-preference-focused-2026-10-01.trx).
- **580 full-suite passes, zero failures, 22 existing skips, 602 total**, in 1 minute 35 seconds: [full results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/movie-tv-source-preference-2026-10-01.trx).
- WPF build: zero warnings/errors. WPF DLL SHA-256: `E5FF2C20A210BF34B36D538E736C04DB988DAD7DC21F3875AADDF2A60B777AD2`; Core DLL: `C9B074764EC79418A2A786961B1AACC45EFB6F09C422A8F94A29B9673EA9A2CA`.
- Python was unchanged. Source and WPF-output audiovisual script hashes remain `FC00AB22F20C54F4DFA258E441AED05965B69C52FB2C6B1B734FA528DD05DBCF`; the preceding 147 Python passes remain a dated baseline, not a new run.

## Live evidence

Checks use production bootstrapper/extractor, matching, proxy and native player services with isolated profiles and muted memory-output video. The TV checks additionally use the public production resolver's progressive updates with an English-audio preference. No main-monitor window is opened. These are service/player checks, not physical catalog/fullscreen journeys.

An initial Dune (2021) run through the collected-list provider returned an independently matched native stream in **22.622 seconds**, rendered loaded English captions, decoded video and passed seek/inactive pause/paused return/explicit resume. Spoken-audio tags were absent. [Report](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/tt1160419-feature-loaded-native-result.json), [native frame](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/tt1160419-feature-loaded-caption.png).

A separate progressive Dune lookup returned four candidates without a confirmed native match. Its first byte-checked native source retained `independent_identity_missing`; the helper intentionally failed that identity requirement. This remains an availability/evidence failure, not a movie acceptance pass. The successful earlier run does not erase it. [Failed run](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/native-progressive.log).

The progressive TV resolver returned an earlier website candidate, then an independently matched native stream with loaded English captions. Native streams became available before completion. Missing audio evidence remained `audio_evidence_missing` under the English preference; the identity/unit check passed independently.

| Item | Native update / selected handoff | Loaded caption bytes | Paused / resumed position |
| --- | --- | --- | --- |
| Breaking Bad S1E1 | 20.772 / 20.914 seconds | 40,779 | 137,934 / 138,538 ms |
| Breaking Bad S2E1 | 20.076 / 20.093 seconds | 31,757 | 103,450 / 104,053 ms |

Both decoded video, selected the English native caption, sought into a timed cue, paused while inactive, stayed paused on return and resumed only on command. Native frames were inspected and show English captions for both episodes. [S1E1 report](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/tt0903747-S1E1-loaded-native-result.json), [S2E1 report](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/tt0903747-S2E1-loaded-native-result.json), [progressive run](C:/Users/user/animeapp/.artifacts/implementation/movie-tv-source-preference-20261001/native-progressive-tv.log).

These network-dependent samples do not prove a general speed or accuracy rate. The deterministic held-alternative tests prove that an early unknown native result remains available without cancelling the later independent match. No live sample in this batch reproduced that exact native-unknown-to-native-confirmed sequence; the separate Dune lookup lacked a confirmed alternative entirely. The memory-output harness retains its existing VLC converter/on-top/subtitle-option warnings; those warnings were not repaired or claimed as actual UI failures.

Remaining: consistent item evidence and native availability across other Movie/TV providers, actual spoken audio/film cuts, full-duration caption timing, second-season/special and provider-change resume journeys, visible catalog/fullscreen flows, DASH validation, HLS/DASH downloads and season downloads. The earlier desktop-testing runtime failure still leaves physical secondary-monitor journeys unvalidated. This batch adds no keys/provider picker, data migration or unrelated feature work.
