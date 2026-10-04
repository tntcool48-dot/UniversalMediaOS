# UniversalMediaOS — implementation checklist

**Updated:** October 4, 2026. **Status:** recovery incomplete; end-to-end and packaged-app acceptance remain open.

This is the sole active progress tracker. Technical contracts remain in the [implementation plan](C:/Users/user/animeapp/docs/IMPLEMENTATION_PLAN.md); original requirements are in the [recovery plan](C:/Users/user/animeapp/docs/APP_RECOVERY_PLAN.md). Older steps and dated failures are preserved in the [history archive](C:/Users/user/animeapp/docs/qa/implementation-checklist-history-2026-10-01.md).

**Next:** bound/coalesce image work and cancel detached consumers; verify reload and body deadlines, then measure growing-grid/multi-tab resources. Progressive Dub results, provider outcomes, shared ownership, stale-generation guards and automatic-filter/Details cancellation passed controlled tests and actual second-monitor checks. [Dub QA](C:/Users/user/animeapp/docs/qa/dub-progressive-availability-2026-10-04.md). **Books are deferred** until the other non-cartoon recovery work is complete; real Anna/PDF/broader reader acceptance stays open, with the Alice EPUB import/restart repair preserved. Arabic cartoons remain last. Catalog-qualified DASH, full TV season availability (**6/7**), physical caption Off/on, broader anime/player/provider/audio/download and release acceptance remain open. All six permanent videos/captions and separate film/episode resume identities remain preserved. **18 grouped unfinished tasks remain.**

## Rules for the remaining work

- October 4 priority: defer remaining Books work until the other non-cartoon recovery work is complete. Preserve the book repairs/data and their open acceptance gates; Arabic cartoons remain last.
- Keep visible testing on the **second monitor** and use isolated profiles. Preserve source, user data and permanent downloads.
- Normal watching uses native streaming or an app-played download. **Open website** is an explicit last resort. TV/anime offer Stream episode / Watch via download / Download season; Movies offer Stream movie / Watch via download / Download to library.
- Keep keyless **Wikidata Movies / TVmaze Television**, without a TMDB default or required key. Anime discovers and rotates Sub/Dub providers through **EverythingMoe and The Index**, without a static provider picker or seed list.
- Preserve exact matching and honest unknown states while improving speed. Request IDs, subtitle language and provider labels alone do not prove item identity or spoken audio.
- Background players pause, retain media/position/document/surface and return paused. Native controls overlay video and hide on idle; website playback has no duplicate app controls.
- Temporary watches last until the final owning tab closes. Prefer Anna's Archive for books. Arabic cartoons are last and remain until a removal decision.
- Fix one concrete user problem per batch. Reuse one build location; retain small QA evidence instead of full copies of each build. Record verified outcomes and the next action here. Avoid speculative tasks and unnecessary architecture.

## Implemented and verified steps

Checked rows describe specific changes, not completion of their whole feature.

- [x] Grouped navigation, shared control styling/density fixes and removal of the static anime provider option. Current UI/accessibility acceptance remains below.
- [x] Independent native/browser player tabs, inactive pause, explicit resume and disposal/white-video lifecycle repairs. Controlled tab/focus/close checks passed. [Player evidence](C:/Users/user/animeapp/docs/qa/player-tabs-2026-09-28.md).
- [x] Native overlay controls, idle/cursor hiding, owner-focus shortcuts and caption clearance; website panel hidden. Scoped fullscreen/PiP checks passed. [Controls](C:/Users/user/animeapp/docs/qa/player-controls-2026-09-28.md), [caption layout](C:/Users/user/animeapp/docs/qa/caption-layout-2026-09-29.md).
- [x] Provider captions, named caption/audio choices and position-retaining reloads; requested Dub group and known-language conflict guards. English/Arabic captions and one English Dub sample passed. [Caption/Dub](C:/Users/user/animeapp/docs/qa/captions-dub-2026-09-28.md), [tracks](C:/Users/user/animeapp/docs/qa/track-selection-2026-09-28.md).
- [x] Anime season/part/unit/ID guards, catalog aliases, bounded ID-backed synonym fallback, removal of redundant waits and validated HLS rendition startup. Scoped Sub 1/2 and Frieren Dub native samples passed. [Matching/speed](C:/Users/user/animeapp/docs/qa/anime-matching-speed-2026-09-29.md), [synonyms](C:/Users/user/animeapp/docs/qa/anime-synonym-fallback-2026-09-30.md).
- [x] Exact-episode torrent Watch via download and Movie/TV direct-file temporary watches, shared leases and permanent-file protection. Real transfers and scoped two-player/cleanup tests passed. [Anime download](C:/Users/user/animeapp/docs/qa/anime-temp-watch-2026-09-30.md), [Movie/TV download](C:/Users/user/animeapp/docs/qa/movie-tv-temporary-watch-2026-10-01.md).
- [x] Keyless paginated Movie/TV catalogs, typed work/unit identity, stale-selection guards and scoped versioned persistence conversion. Broad catalog and real-profile acceptance remains below.
- [x] Progressive Movie/TV source UI, matched native-source preference, nested-frame extraction and original item/caption handoff. Unknown candidates remain usable while verification continues. Dune (2021) and Breaking Bad S1E1/S2E1 passed scoped native caption/seek/pause/return/resume. [Latest QA](C:/Users/user/animeapp/docs/qa/movie-tv-item-readiness-2026-10-01.md).
- [x] Isolated storage, shared Python preparation, per-service health/repair and uBlock activation fixes. Prepared-profile startup and extension loading passed; packaged repair acceptance remains below.
- [x] Actual second-monitor Wikidata/TVmaze → native stream journeys for Dune (2021), Breaking Bad S1E1/S2E1: correct item/unit, visible English captions, seek, paused tab return and sampled fullscreen. Visible downloads remain below. [Desktop/resume QA](C:/Users/user/animeapp/docs/qa/movie-tv-ui-resume-2026-10-01.md).
- [x] Movie/TV provider/URL-change and restart persistence with immutable work/unit writes, cross-player ordering, completion protection, fresh SQLite reads and live library summaries. Physical film/two-season restart samples and final saved TV chooser passed; adjacent/established special units and changed providers passed controlled regressions. Existing positions remain separate. [Resume QA](C:/Users/user/animeapp/docs/qa/movie-tv-ui-resume-2026-10-01.md).
- [x] Anime canonical catalog/episode resume through Sub/Dub URL changes, native/browser handoffs, adjacent navigation and temporary-download context. Exact MAL legacy imports preserve old rows; ambiguous title/URL rows remain untouched. Second-monitor Frieren episodes 1/2 passed native next/previous, older-tab close and separate process-restart positions. Same-title/no-MAL collisions and completion passed controlled regressions. Real-profile data safeguards remain below. [Anime resume QA](C:/Users/user/animeapp/docs/qa/anime-canonical-resume-2026-10-02.md).
- [x] Anime download discovery reads AnimeTosho description magnets, counts matching usable units and searches Dub/dual-audio releases before seeder ranking. Visible cancellation removed owned jobs. Metadata-only preparation and short completed filenames repaired early startup and a real 351-character native open failure. The second-monitor first-season Frieren episode-1 dual-audio download passed native video, canonical resume, seek, immediate shared-file reuse, first-owner retention and final-owner deletion. Spoken audio and full caption selection remain below. [Download QA](C:/Users/user/animeapp/docs/qa/anime-download-ui-2026-10-02.md).
- [x] Finite-HLS Movie/TV temporary downloads save bounded selected media/audio resources, assemble and duration-check a short local MKV, and hand off only saved timed sidecars. Bounded transient request retries repaired a real film timeout; dialogs now belong to the app window. Actual second-monitor Breaking Bad S2E1 and Dune (2021) samples passed native frames/English captions, separate resume, seek, immediate two-player reuse, first-owner retention and final-owner deletion. Caption switching, provider cuts/audio, DASH and permanent season/library paths remain below. [Playlist QA](C:/Users/user/animeapp/docs/qa/movie-tv-playlist-download-2026-10-02.md).

- [x] Bounded finite, unprotected MP4 DASH temporary downloads localize templates/lists and retain selected video/audio tracks. Real generated media passed production remux/probe and decode with two tagged audio tracks, shared-file reuse and final-owner deletion. Live MPD catalog/provider/UI qualification remains below. [DASH QA](C:/Users/user/animeapp/docs/qa/dash-download-2026-10-03.md).

- [x] Verified numbered TV season and Movie/TV permanent publication, local metadata/caption handoff from Downloads, completed-unit reuse and bounded empty-body/advertised-rendition recovery. Second-monitor TV season saved six exact units, kept them on episode-7 failure, and opened S1E1/S1E2 with separate resume. Final restart reused all six; cancel retained them. Distinct episode titles, paused seek status and paused tab return passed physically. Full-season availability and live DASH remain open. [Season/library QA](C:/Users/user/animeapp/docs/qa/season-library-download-2026-10-03.md).

- [x] Bounded finite DASH initialization/media sampling in Python, captured content-type handoff and session-owned native MPD/resource rewriting. A real public stream initially failed on LibVLC's version-1 unknown movie-duration sentinel; bounded initialization normalization repaired it. Second-monitor native frames, seek-on-resume and paused tab return passed. Controlled real decode and independent session cleanup passed. Exact catalog DASH/download, spoken audio and broader format/caption acceptance remain open. [DASH native QA](C:/Users/user/animeapp/docs/qa/dash-native-validation-2026-10-03.md).

- [x] Bounded HLS/DASH sessions, simultaneous requests, small-file bodies, media inactivity/overall transfers and manifest rewrite growth. Source-switch/close cancels only owning sessions; stop/dispose cancels all, with bounded async draining. Exact headers/redirects/ranges/TS guards remain. Controlled real two-player HLS/DASH decode and stalled-transfer tests passed; actual second-monitor DASH tabs, older-owner close, seek, stream-to-local replacement and paused return passed. Full-duration/overnight/all-provider/release acceptance remains open. [Lifetime QA](C:/Users/user/animeapp/docs/qa/proxy-request-lifetimes-2026-10-03.md).

- [x] TV season failures can try up to three independently verified media candidates, with failed-path exclusions reaching scraper discovery before first-match cancellation. Unknown requested audio, wrong units, request echoes/websites, local failures and cancellation cannot bypass the guards. Controlled saves/cleanup passed; the actual second-monitor retry exercised two replacements and retained 6/7 after remaining empty resources. [Failover QA](C:/Users/user/animeapp/docs/qa/season-provider-failover-2026-10-04.md).

- [x] GitHub native-test setup now prepares FFmpeg/ffprobe explicitly. All five initial CI failures were missing-tool errors. Season-failover commit `009c131c` passed the hosted build, 743 C# tests with 22 existing skips and Python syntax validation; this predates the book changes. [CI evidence](C:/Users/user/animeapp/docs/qa/ci-native-tools-2026-10-04.md).

- [x] Book Details now resolves its Anna's Archive dependency. The provider rejects first-result guesses, ambiguous resources and conflicting requested edition evidence; selected available files show rights unverified and retain supplied MD5 through reading history. Downloaded/cache bytes are checked before new publication, with existing EPUB extraction safeguards retained. Controlled matching, hash/format/cache, cancellation, history and registration checks passed. Physical second-monitor Matilda Details opened after its earlier critical error and settled with no readable edition; actual Anna download → reader/restart remains below. [Book QA](C:/Users/user/animeapp/docs/qa/book-edition-integrity-2026-10-04.md).

- [x] Progressive book provider pages/outcomes, stable duplicate enrichment, caller cancellation/timeout distinction, body deadlines and stale-query/import guards. Controlled checks passed; actual second-monitor searches retained usable cards through unavailable catalogs, including the final tested build. Reader/Details/restart acceptance remains below. [Progressive book QA](C:/Users/user/animeapp/docs/qa/book-progressive-search-2026-10-04.md).

- [x] Honest progressive Details edition outcomes and Cancel, current Anna mirror routing, saved/local/Anna preference, and EPUB unread/completion/loading repairs. Actual second-monitor Alice import rendered chapters, navigated, saved and restored the same chapter/file at 20% after restart; final-build Matilda reported incomplete lookup and canceled visibly. Real Anna/PDF/broader reader acceptance remains below. [Reader/availability QA](C:/Users/user/animeapp/docs/qa/book-reader-availability-2026-10-04.md).

- [x] Progressive Dub availability publishes exact-card summaries and completed provider evidence without waiting for slow dependencies. Typed outcomes, verified-confidence preservation, MAL-conflict withdrawal, short incomplete-cache lifetimes, shared cancellation, ordered generation guards and canceled-page retry are implemented. Actual second-monitor automatic filtering retained 36 cards on Cancel; Details retained verified episodes 1–11 after Cancel, and Frieren retained 28 verified episodes through a failed dependency. Badge/flag evidence does not qualify spoken audio; captured filter-label and broader selectors remain below. [Dub QA](C:/Users/user/animeapp/docs/qa/dub-progressive-availability-2026-10-04.md).

**Latest recorded local verification:** 824 C# passes, zero failures, **22 existing skips**; latest Python full suite remains 178 passes, zero failures (Python unchanged this batch); build zero warnings/errors. [C# results](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/TestResults/dub-progressive-full-2026-10-04.trx), [Python results](C:/Users/user/animeapp/.artifacts/implementation/book-reader-20261004/python-full.log). The prior book commit `098f355` passed GitHub Release validation, including 809 C# tests with 22 skips. Remote CI is tracked separately from physical acceptance.

Latest Movie/TV and anime checks include actual second-monitor catalog/native playback and restart interaction. Actual spoken audio, full-duration reliability and all-provider acceptance remain open. Network timings are samples, not general speed or accuracy guarantees.

## Remaining work

### Anime and shared player — A/B/C/D, IP02–04/IP07–08/IP11/IP17

- [ ] Verify playing-video Alt-Tab/minimize/restore, native/browser/mixed tab return, browser PiP recovery, physical seek/selectors/shortcuts, supported sizes/themes/DPI/RTL and stable resources. Preserve paused return without white video.
- [ ] Qualify broader real Sub/Dub providers, spoken audio, embedded/provider/sidecar captions and authored/multiline cues; preserve position and requested language through retry, track and provider changes. Keep unavailable/unknown states honest.
- [ ] Complete special/provider-unit mapping and safe lookup without aliases/observed IDs. Verify next/previous UI, first frame, worst-case provider failure/cancellation and AniList catalog-outage behavior without weaker matching.

### Watch via download — E, IP07/IP11/IP17

- [ ] Qualify physical caption switching and broader visible Movie/TV download journeys on the second monitor. TV S2E1 and Dune (2021) finite-HLS download/local English captions/resume/reuse/final deletion passed. The selector lists downloaded English and Off; the latest permanent-episode check displayed a two-line English cue and restored its own position, but desktop-tool activation dismissed the popup before Off/on selection. [Caption check](C:/Users/user/animeapp/docs/qa/downloaded-caption-check-2026-10-03.md). Anime episode-1 dual-audio native playback/reuse/final deletion passed; actual spoken Dub and full caption selection remain to qualify. [Playlist QA](C:/Users/user/animeapp/docs/qa/movie-tv-playlist-download-2026-10-02.md).
- [ ] Check provider/magnet failure, disk limits, cancellation/stale handoff and coexistence with permanent season/library downloads. Clean only owned temporary files.
- [ ] Qualify independently matched catalog DASH Watch via download/native playback and complete TV season availability with exact item/unit/audio/captions and bounded transfers. Public finite DASH segment sampling/native frames passed physically; controlled finite-DASH remux/decode/ownership passed. Finite HLS/local sidecars passed actual film/TV temporary handoff and final cleanup. TV season/permanent publication and restart reuse are implemented; October 4's bounded verified-source failover still retained 6/7 after S1E7's missing resources. Broader/full-season and specified-audio acceptance remain. [DASH native QA](C:/Users/user/animeapp/docs/qa/dash-native-validation-2026-10-03.md), [season/library QA](C:/Users/user/animeapp/docs/qa/season-library-download-2026-10-03.md), [failover QA](C:/Users/user/animeapp/docs/qa/season-provider-failover-2026-10-04.md).

### Movies and Television — F, IP04–08/IP10–11/IP17

- [ ] Qualify other providers and independent Python/manifest/Archive evidence, exact remakes/cuts, audio, full captions and sustained native availability. Retain fast partial results, conflict rejection and bounded/cancelled work across tabs. Finite DASH sampling/native proxy passed a public transport sample; exact catalog DASH remains unqualified.
- [ ] Fix demonstrated film/animated-film coverage, browse relevance/latency/artwork and provider-ID reconciliation. Keep useful paging and non-TMDB defaults; retain the Your Name search miss as a coverage case.

### Books — deferred, G, IP13/IP17

- [ ] Qualify real Anna's Archive exact hash/ISBN/edition/language/format and EPUB/PDF download-to-reader availability without a new mandatory key. Current official mirrors returned HTTP 403 browser checks; real Anna bytes remain unqualified. Conservative matching, byte/hash/cache checks and truthful rights remain. Details now publishes available links through provider failure, exposes Cancel and distinguishes incomplete lookup from no edition; actual second-monitor Matilda passed these states. OpenLibrary work-level edition fields and broader file structure remain unqualified. [Edition QA](C:/Users/user/animeapp/docs/qa/book-edition-integrity-2026-10-04.md), [availability QA](C:/Users/user/animeapp/docs/qa/book-reader-availability-2026-10-04.md).
- [ ] Complete PDF and broader reader navigation/progress acceptance; preserve imports/data. A real Alice EPUB import passed physical chapter selection/Next/Previous and close/restart → Continue Reading → same chapter/file at 20%. Merely opening a chapter no longer marks it complete; canceled/old loads cannot replace the current reader. Resume is at chapter start, percentage is estimated, and this EPUB's picker still uses generic spine labels. [Reader QA](C:/Users/user/animeapp/docs/qa/book-reader-availability-2026-10-04.md).

### Arabic cartoons — last, IP05–06/IP17

- [ ] Replace transitional discovery with qualified Flona/Heidi-type sources: aliases, actual Arabic audio, version/episode order and shared-player behavior. Preserve Cartoons/data until the retention/removal decision.

### Performance, services, UI and data — IP01–03/IP08–09/IP12–17

- [ ] Bound/coalesce image work, cancel detached consumers, restart reloads and include body deadlines. Virtualize the growing grid; measure large-list/multi-tab resources before broader changes.
- [ ] Measure dispatcher/database contention and fix demonstrated blocking writes. Preserve player ordering, completion guards, bounded final flush and last-unit summaries; reconcile only verified canonical/legacy aliases. Verify real-profile migration, backup and rollback without guessed merges.
- [ ] Verify Settings save/reload/migration, localization/accessibility, action states, health/Repair and packaged Python/FFmpeg/uBlock startup/shutdown. Check runtime/script invalidation and CLI compatibility. Investigate historical staging denial only if it recurs; preserve secrets/overrides.
- [ ] Check preserved Downloads restart/pause/resume/cancel, My List, manga, optional MAL/voice/notifications and two-client Watch Together reconnect. Repeat player/download/reader cycles to check resources; record actual failures before changing preserved features.

### Release — IP18

- [ ] Inventory 22 skipped tests; cover release-relevant gaps or document exclusions. Run meaningful regressions for final changes.
- [ ] Produce the final source/script/asset/native-dependency manifest and readable build identity. Smoke-test the packaged executable with fresh/copied legacy profiles, migration/backup/rollback and services.
- [ ] Link final media/UI/data journeys to original requirements and document accepted external/product limitations. Close release acceptance only after behavior is demonstrated.

## Current testing limitation

The supported desktop tool now executes and the Movie/TV samples above used it. Cached snapshots, window movement and occlusion sometimes require fresh observation; rejected inputs are not acceptance evidence. Broader physical UI and packaged-app gates remain open. Visible work stays on the second monitor.

## Checklist cleanup — October 1

Removed repeated summaries/test totals, superseded failures, completed tasks presented as open, duplicate release gates and internal bookkeeping without a user-visible outcome. Substantive unfinished behavior remains grouped above; detailed history stays linked. This cleanup did not mark any feature complete.

Storage cleanup also completed: 853 obsolete generated folders removed, zero failures, 51.405 GB reclaimed; workspace reduced to 7.453 GB. Current app/test builds and protected QA evidence remain; WPF/Core hashes are unchanged. [Cleanup results](C:/Users/user/animeapp/docs/qa/storage-and-checklist-cleanup-2026-10-01.md).
