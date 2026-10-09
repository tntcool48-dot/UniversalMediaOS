# Universal Media OS — full implementation plan

**Date:** September 9, 2026  
**Status:** Implementation in progress on `codex/app-recovery`; end-to-end app recovery remains unverified. Use the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) for current progress and the next action. Checkpoints below are dated implementation evidence, not full-feature completion.  
**Requirements:** [App recovery and improvement plan](C:/Users/user/animeapp/docs/APP_RECOVERY_PLAN.md).  
**Scope:** Repair the current Windows WPF application and its Python/service integration. This document defines the code changes, dependencies, migration strategy, tests, and release process. Production changes are now being implemented against the preserved source snapshot.

## Thirty-second implementation checkpoint — bounded anime synonym fallback

**September 30, 2026: D / IP07/IP11.** AniList supplies `synonyms`, but playback previously sent only English and romaji titles; when both were absent the catalog displayed `Unknown Title`. Keep the normal known-title search first. If it has no distinct English/romaji alternate and misses, select at most one short, searchable, season/form-compatible catalog synonym, favoring overlap with the requested title, and try it within the same indexed site and deadline. A page identified only by that synonym must expose a matching AniList/MAL ID before extraction; recommendation links and unrelated page titles are insufficient. Retain the original requested episode, form, language and media validation. Native catalog title now replaces the fabricated `Unknown Title` when English and romaji are absent. A native-only title without a safe searchable synonym fails early instead of crawling unrelated pages. No new provider, key, user option, index entry or network call on a successful normal-title search is added.

**Validation:** production Debug build zero warnings/errors; **495 selected C# and 122 Python tests passed**, zero failures/skips. The fixture matrix covers synonym ranking and limits, normal-title success without added requests, fallback search within the existing site deadline, native-only titles, matching/missing/conflicting provider IDs and season/episode conflicts. A final-build offscreen lookup simulated a catalog entry with only `Sousou no Frieren` and the same entry's known English title as a synonym. It searched the fallback after a miss and returned a native selected-Dub stream with one caption, consistent episode 1 and a matching MAL ID from the provider page in **16.756 seconds**. This is one network-dependent service result, not a controlled speed/accuracy rate or a new rendered/listened player check. [QA evidence](C:/Users/user/animeapp/docs/qa/anime-synonym-fallback-2026-09-30.md).

**Remaining:** entries with no safe synonym or provider ID, unknown/special unit mapping, wider provider/outage/cancellation and A/B/C/D player/resume/presentation acceptance. Keep the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) as the only progress tracker before moving to the agreed download, Movie, TV, book and cartoon work.

## Thirty-first implementation checkpoint — optional catalog-ID conflict checks for anime

**September 30, 2026: D / IP07/IP11.** The anime catalog already has AniList and often MAL IDs, but source resolution previously passed only titles and episode number. Carry those IDs as optional, keyless context through Stream, explicit website and adjacent-episode resolution. On the currently visited provider page, inspect only the active episode's explicit ID attributes and primary heading/head metadata or canonical URL. A conflicting observed ID rejects the page before any media capture; a matching ID is recorded as independent evidence. The title, season, form and episode guards remain required and an ID cannot override their conflicts. Pages without an exposed ID keep their prior matching behavior. No provider picker, additional network request, mandatory key, storage change or broader parser is added.

**Validation:** production Debug build zero warnings/errors; **495 selected C# and 113 Python tests passed**, zero failures/skips. The new fixture checks cover ID forwarding, matching/missing/conflicting IDs, wrong-title or wrong-episode rejection despite a matching ID, URL namespace restriction and stopping before capture. The provider-page JavaScript passed Node syntax checking. This batch did not establish that a live provider exposes an ID or run a new player/first-frame check, so independent-ID live coverage and anime acceptance remain open. [QA evidence](C:/Users/user/animeapp/docs/qa/anime-provider-ids-2026-09-30.md).

**Next:** verify which automatically discovered providers actually expose independent IDs, then address entries without usable catalog aliases and unknown/special unit mapping within the D gate. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) remains the sole tracker; the other agreed media work follows anime watchability.

## Thirtieth implementation checkpoint — catalog-title aliases for anime streams

**September 30, 2026: D / IP07/IP11.** The catalog already contains English and romaji titles for one anime entry, but player resolution sent only `OfficialTitle`. The romanized Frieren request previously missed source links and failed after 112.807 seconds, while its English title resolved. Carry both existing catalog spellings through normal Stream, explicitly chosen website and adjacent-episode resolution. Prefer a compatible English search term when the requested title is romaji; accept compatible catalog titles in page relevance, own-card URL/labels and primary episode heading. Keep one automatic index/provider pool, the existing lookup budgets and original requested unit/form/episode checks. Do not add provider options, mandatory keys or data migration.

A new regression exposed an existing heading gap: a different anime's descriptive heading could pass as unknown if it contained the requested episode number. Reject that title conflict while permitting a generic episode-only heading when the page's own title matches. One-word/generic, long or version-conflicting alternate titles are not accepted. Independent provider IDs and entries without a usable alternate remain open.

**Validation:** final production build zero warnings/errors. **495 selected C# and 106 Python cases passed, zero failures/skips**, adding two C# and eleven Python cases. Fresh final-build production-service lookups of **Sousou no Frieren** with its English catalog alias and the canonical English title resolved selected-Dub native media in **15.826 and 17.400 seconds**. Both had consistent episode-1 evidence, one caption and a validated HLS variant. The romaji request recorded an alias-backed title match. This resolves the observed case; the previous failure and current results are network-dependent samples, not a general benchmark or measured accuracy rate. Spoken Dub language remains untagged and was not newly listened to. The isolated service diagnostic routes captured final results through the production router/proxy; it does not render a new player frame or use agent OS input/activation. Eleven unit/media/audio validation blocks are unchanged in the before/after syntax comparison. [QA evidence](C:/Users/user/animeapp/docs/qa/anime-title-aliases-2026-09-30.md).

**Remaining:** independent source IDs, entries without aliases and special/unknown unit mapping, next/previous UI, returned-frame/focus/resource, changed-provider/restart resume, broader provider/outage/cancellation and A/B/C acceptance. Then follow E temporary downloads, F native Movie/TV, G Anna's Archive/Arabic cartoons and shared service/performance/release checks. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) marks this scoped implementation complete while retaining the anime gate as open.

## Twenty-ninth implementation checkpoint — native streaming and explicit website fallback

**September 29, 2026: D / IP11.** Browser-only captures previously stopped inner extraction and the provider pool, then opened a website for an ordinary Stream request. Save the first eligible browser candidate and continue native extraction across stages/frames/selected-audio servers, candidates and automatic current-index providers within the existing limits. The native router never opens captured or saved website URLs after failure. The separate **Open website** last-resort action explicitly requests website mode; Stream remains the default and adjacent-episode resolution retains the chosen mode. Keep matching/navigation/media/Dub guards, actual captions and validated HLS variant handoff; add no provider picker, required key, dependency or data migration.

**Validation:** final production build zero warnings/errors; **493 selected C# and 95 Python cases passed, zero failures/skips**, including eight added C# and 14 added Python cases. Fresh actual production-service Sub and English-title Frieren Dub returned native streams with captions and validated variants in 19.882 and 19.766 seconds. Explicit website resolution/handoff took 20.836 seconds; four-second caller cancellation propagated in 4.151 seconds. Matching/media helpers and both audio guard branches are unchanged in the before/after syntax comparison. The isolated service diagnostic uses final production assemblies/scripts; the router consumes captured service results separately. It does not render a fresh player frame or independently listen to Dub audio. No agent desktop input/activation was used; no owned worker remained. Timings are individual network-dependent observations, not a controlled speed or accuracy benchmark. [QA evidence](C:/Users/user/animeapp/docs/qa/anime-native-policy-2026-09-29.md).

**Remaining:** an alternate romanized **Sousou no Frieren** query failed after 112.807 seconds while the English catalog title succeeded. Preserve that alias gap and unknown evidence instead of relaxing title/audio verification. Finish aliases/independent IDs/special units, broader provider/first-frame/outage/cancellation, next/previous UI, returned-frame/focus/resource and changed-provider/restart resume and A/B/C acceptance. Then proceed with the agreed temporary downloads/native Movie/TV/Anna's Archive/Arabic-cartoon sequence and shared release checks. Seven user-facing work areas remain; the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) records the scoped completion without marking the anime gate complete.

## Twenty-eighth implementation checkpoint — matching, lookup time and validated native rendition

**September 29, 2026: D / IP07/IP11.** Reject explicit season/part/cour/form conflicts in candidate URLs or their own card labels. Replace neighboring-card context with anchor text/title/image alt, exact numeric episode matching and supported path/query rewrites that do not retry a known-wrong unit. Check primary source title/selected episode before entering the player; reject observed conflicts and record unknown evidence honestly. Failed navigation cannot reuse the previous DOM/stream. Remove ready-player sleeps and empty server-parent listening, batch server metadata, disable hidden per-navigation retries, skip confirmed error pages and pass remaining deadlines into inner controls/listening. Overall lookup budgets stay unchanged.

The native check found that a validated 1080p rendition could still fail at zero because VLC selected an advertised lower rendition whose segment returned 404 HTML. Carry the actually validated advertised variant through the existing scraper protocol/proxy selector, retaining master audio/subtitle groups and legacy results. No provider picker, static domain, mandatory key, dependency or data migration is added.

**Validation:** final build zero warnings/errors; **485 selected C# cases and 81 Python cases passed, zero failures/skips**, including two new C# and 33 new Python cases. Same cached-index episode-1 lookup improved from **19.422 to 16.812 seconds**; final Sub episode 2 took 16.579 seconds and Frieren Dub 16.828 seconds. These are individual network-dependent observations; the historical 43-second lookup is a different provider baseline. All final streams carry consistent primary-page/selected-unit observations and actual captions; Dub language remains untagged. The final second-monitor native diagnostic completed three independently retained players with no playback/action errors, native advance/seek/pause, inactive pause and first-tab position/media retention and resume. Actual Sub video and Dub video with an English caption were visible. Failure traces, measurement scope and the missing returned-frame capture are retained in [QA evidence](C:/Users/user/animeapp/docs/qa/anime-matching-speed-2026-09-29.md). No agent OS keyboard/mouse/activation input was used.

**Remaining:** aliases/independent provider IDs and unknown units, other/deeper nested qualities, worst-case outage/cancellation and first-frame latency, explicit website fallback, next/previous UI and changed-provider/restart resume, physical focus/presentation/resource and remaining A/B/C acceptance. A separate Mushoku-season Dub probe returned unavailable/403 media; do not treat Frieren's successful Dub as proof for it. Anime's full gate and subsequent temporary downloads/native Movie/TV/books/cartoons remain open. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) records the completed scoped steps and next work.

## Twenty-seventh implementation checkpoint — readable captions and native fullscreen

**September 29, 2026: B/C / IP02/IP03/IP11.** Move native title/options to the top of the overlay and bound expanded options with scrolling. Keep transport above the default bottom caption area when captions are selected, accounting for the fitted picture/letterboxing and WPF viewport units. Off returns transport to the bottom. Keep the video surface and native caption renderer; revealing, expanding and hiding controls do not reopen the stream. Limit hover/input checks to actual panels so the transparent overlay still permits idle hiding and video interaction.

**Validation:** production build zero warnings/errors; **483 selected C# tests passed, zero failures/skips**, including four responsive layout cases. On the second monitor, native fixture Arabic cues remained readable with expanded options in normal/paused fullscreen. Actual English and Arabic dialogue rendered with production controls visible; actual playing fullscreen hid all controls/chrome and revealed controls through the production pointer handler without changing video bounds/media. Native pause/fullscreen exit/PiP/exit and caption Off checks completed. [QA evidence](C:/Users/user/animeapp/docs/qa/caption-layout-2026-09-29.md) records source/fixture scope, traces and remaining limitations. No agent keyboard, mouse or activation input was used; Python/source resolution were unchanged.

**Remaining:** real playing focus/minimize/restore, physical selector/edit/shortcut interaction, browser PiP/recovery, monitor/DPI changes, authored/styled captions and mixed/provider/audio coverage. This closes the demonstrated bottom-caption overlap; it does not complete A/B/C or anime acceptance. Continue D's exact matching, automatic lookup latency, resume and cleanup before the agreed temporary download/native Movie/TV/book/cartoon batches. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) carries the current status.

## Twenty-sixth implementation checkpoint — named tracks and retained quality state

**September 28, 2026: C / IP07/IP11.** Add named subtitle/audio lists under Playback options. Select the actual provider caption file with its own request context; load one file rather than guessing that generic decoder numbers identify asynchronously loaded languages. Remember unique caption identity across episode reorderings; preserve Off when the remembered external choice is unavailable. Native audio uses declared language/description when unique and removes decoder-local Disable/deleted entries.

Quality/caption reload and native retry preserve current position, caption/audio choices, quality options and pause intent. Establish seek/pause state before staging input, wait for the native seek clock before completing a paused reload, use the current clock for queued updates and remove the stale deferred quality continuation. Preserve early quality discovery and allow recovery from a genuine playback error.

**Validation:** production build zero warnings/errors; 479 selected background C# cases passed, zero failures/skips, with seven added cases. Python was unchanged; the preceding 48 passes are historical. Controlled native 180p/360p, English/Japanese audio, caption Off/retry and paused/playing position checks passed. Actual English and Arabic caption cues rendered after paused named switches. A small native-clock advance primes the caption decoder; two switches added 0.980 seconds in the measured hold. Evidence and limitations are recorded in [QA](C:/Users/user/animeapp/docs/qa/track-selection-2026-09-28.md). Local quality options bypass public discovery; live anime sources still expose one variant and untagged audio. Visible work uses isolated profiles on the second monitor without agent keyboard/mouse/activation input.

**Remaining:** caption/control overlap and B presentation/focus/PiP checks; broader mixed-track/provider/audio listening and D exact matching/search latency/resume/cleanup. External caption changes reopen native input and incur buffering cost. Anime and full-app acceptance remain incomplete; download actions, native Movie/TV, Anna's Archive and Arabic cartoons retain their agreed order. The [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) remains the only active checklist.

## Twenty-fifth implementation checkpoint — usable Dub and native caption state

**September 28, 2026.** The prior strict audio-tag gate rejected a usable English Dub. Accept validated untagged media from the actually selected, single changed Dub iframe while keeping its language unknown; known non-English metadata still rejects it. Carry a provider-label language notice through initial/adjacent handoff, retry and quality reload. Explicit audio groups override ambiguous text, and audio cycling excludes VLC Disable and disables switching for one real track.

Final build: zero warnings/errors; **472 selected background C# tests and 48 Python cases passed**, with four new C# cases, six new Python cases and a notice-retention assertion. Fresh direct episode-1 Dub resolved in 19.89 seconds; episode 2 in 19.70 seconds; Sub in 20.39 seconds. A bounded local speech sample confirms coherent English dialogue without adding an audio tag or product dependency. Native Dub seek/retry/next/previous, native Sub dialogue rendering and caption off/seek/retry/on checks passed on the second monitor without OS input. Both live masters offer one variant, so multi-quality/audio and named caption selection remain open alongside B/D acceptance. Catalog/search UI and independent exact-unit matching were bypassed by the native diagnostic.

See [QA evidence](C:/Users/user/animeapp/docs/qa/dub-selection-2026-09-28.md) and the [authoritative checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). This is a focused correction and acceptance checkpoint, not completion of anime or the other media.

## Twenty-fourth implementation checkpoint — native captions and requested audio

**September 28, 2026: C / IP07/IP11.** Carry actual caption tracks and their request headers/cookies/referer from Python DOM/player/config/network capture through the scraper protocol, router, initial/adjacent-episode handoff, retry and quality changes. Attach native subtitle slaves through bounded, owned caption proxy sessions and release those sessions when media is replaced/closed. Prefer English captions; retain manual caption/audio selection during native reloads. Keep HLS audio/subtitle groups when selecting a resolution.

The reproduced Dub error accepted a Japanese-only default stream in Stage D. Bounded TS/MP4 metadata and validated HLS audio renditions now feed English-Dub verification in all extraction stages and native C# routing. Read the controls' parent audio group, stop after selecting the requested Sub/Dub server and rotate the current same-language controls past deleted-file hosts. Live native tests also exposed wrong caption referrers, dropped cross-CDN public context and valid TS segments labeled HTML/JavaScript. Replay the captured public caption context, preserve origin-only context across CDN changes while restricting credentials to their host, and inspect TS bytes before rejecting those MIME labels. Preserve automatic EverythingMoe/The Index discovery; no static provider picker, new key or storage migration is introduced.

**Verification:** zero build warnings/errors; **468 selected C# tests passed, zero failures/skips; 42 Python cases passed**. Sixteen new C# and nineteen new Python cases cover captions, HLS groups, wrong/unknown/null Dub metadata, controls/rotation and mislabeled segments; existing credential-boundary/decoy regressions are retained and updated for the observed behavior. Desktop-input classes are excluded while the user works on the primary monitor. Native fixture cues changed with time; the final production assemblies then played the real Frieren stream with visible timed English captions and nine native caption tracks on the second monitor, with no agent input/activation. Three direct Sub probes resolved native media and captions in 18.98, 18.56 and 16.36 seconds; these known episode routes are not full automatic-search benchmarks. [QA evidence](C:/Users/user/animeapp/docs/qa/captions-dub-2026-09-28.md), [selected TRX](C:/Users/user/animeapp/.artifacts/implementation/captions-dub-build/results/captions-dub-media-final.trx), [manifest](C:/Users/user/animeapp/.artifacts/implementation/captions-dub-checkpoint.json).

**Remaining:** an audible English Dub scene and track-selection journeys remain open. A selected Dub host returned file-removed 410; another served valid AAC without a language tag, confirmed independently with ffprobe, and did not pass the strict English gate. Earlier native attempts failed at zero or 4.3 seconds before the live CDN/MIME repairs; those failures remain recorded. Failed lookups still exceeded nominal budgets. B/D full presentation, matching, automatic search, latency, resume and cleanup acceptance and Movie/TV/download/book work remain open. Successful visible checks use the second monitor; the active checklist remains the only progress checklist.

## September 28 stream and download action amendment

The user clarified that app-played streams and downloaded files are the normal watch experience. Opening a provider website is an explicitly chosen last resort and must not count as successful streaming. This supersedes older acceptance wording that treated native and site playback as equivalent Movie/TV success.

| Media | Normal watch actions | Permanent download action |
| --- | --- | --- |
| Anime / Television | Stream the selected episode; Watch via download of that exact episode | Download season |
| Movies | Stream movie; Watch via download of that exact film/cut | Download to library |

Offer the temporary download choice before a stream fails. Reuse existing source-result contracts, RSS, embedded MonoTorrent and the queue; wait for the selected file rather than playing the first completed file. Keep temporary files while any owning player tab remains open, then release handles and delete only owned cache data. Permanent downloads have separate ownership and survive tab closure. Exact title/unit/audio/caption evidence, cancellation, progress, limits and restart cleanup are prerequisites for acceptance. Streaming is not automatically replaced by downloading.

After anime's remaining gate, implement native Movie/TV source actions and temporary-watch ownership together where they share the existing player/queue path. Then qualify Anna's Archive exact editions and actual EPUB/PDF reading/progress. Old Arabic-dubbed anime remains last. Keep Wikidata films, TVmaze television, minimal key requirements and automatic anime indexes. These actions are agreed work, not completed implementation.

## Twenty-third implementation checkpoint — native overlay controls and focus recovery

**September 28, 2026: B / IP02/IP03/IP11.** Move the native transport panel into VLC's overlay and remove its reserved layout row. Use compact transport/volume/presentation controls over video, with advanced and source actions under Playback options. Hide the panel/cursor after idle playback without resizing video; retain controls during seeking, expanded options and keyboard interaction, reveal on pause/error/buffering/return, and cancel stale fade completions. Preserve site-panel hiding and inactive-overlay isolation.

Live testing caught K not pausing after owner activation. Handle active native shortcuts in both VLC's foreground overlay and the owner window, preserve source/selector input and restore the app with Escape while editing. Allow site PiP via tab-focused P while leaving page input to the provider; explain the action in the tab tooltip. Correct primary-button hover text contrast.

**Verification:** zero build warnings/errors; **472 tests passed, 22 existing skips, zero failures**, including two additional cases for small-viewport overlay layout and owner-focus/source-editing keyboard recovery. Candidate and final normal-playback traces showed native idle hiding with unchanged 1280×653 bounds and cursor None. The final build demonstrated native K play/pause, retained visible paused frames through resize and native fullscreen filling 2560×1440 without app chrome. Two final iframe pages loaded without the app panel. [QA evidence](C:/Users/user/animeapp/docs/qa/player-controls-2026-09-28.md), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/player-controls-build/results/player-controls-final.trx), [manifest](C:/Users/user/animeapp/.artifacts/implementation/player-controls-checkpoint.json). Python/provider extraction were unchanged.

**Remaining:** the user's physical Escape stopped Computer Use; no further desktop input was issued. Playing fullscreen idle/reveal, native/site PiP/recovery, interaction/DPI and broader real-anime/resource acceptance remain open. The diagnostic app was left open and its temporary HTTP server stopped. Next source implementation is C's real caption handoff and verified actual Dub; this checkpoint does not complete anime or the other media. The [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) is still the only progress checklist.

## Twenty-second implementation checkpoint — retained player tabs and site controls

**September 28, 2026: A / IP03/IP11/IP17 and B's site-panel requirement.** Replace the shared active-content player view with one retained view per open player. Pause outgoing tabs, reject late background play and preserve native surfaces/browser documents until final close. Check/repair the native HWND binding on return or window activation. Reveal native controls on return for explicit resume, avoid startup failure before queued media's first Play, and dispose removed views before their scopes.

Hide the entire duplicate app transport/source panel in site mode and collapse native overlays belonging to inactive tabs. Live testing also reproduced the first browser navigation being aborted when the second tab re-added uBlock to their shared profile; serialize/reuse successful extension initialization. Keep failures retryable.

**Verification:** zero build warnings/errors; **470 tests passed, 22 existing skips, zero failures**, including 13 additional cases. Controlled production-component playback covered four open players, native/native and browser/browser paused return, mixed selection, paused native Alt-Tab, provider fullscreen without duplicate app controls and active/inactive tab cleanup. The final build repeated native pause/return/explicit resume. [QA evidence](C:/Users/user/animeapp/docs/qa/player-tabs-2026-09-28.md), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/player-tabs-build/results/player-tabs-final.trx), [manifest](C:/Users/user/animeapp/.artifacts/implementation/player-tabs-checkpoint.json). Python and provider extraction were unchanged.

**Remaining:** broader A acceptance through real anime, playing-video focus/minimize/restore, resource settling and monitor/DPI changes. B's native controls still reserve a layout row and resize video when hidden; implement the proper native overlay/fullscreen/idle behavior next, then C's caption handoff and verified actual Dub. Final-build browser PiP is also unverified. These changes do not complete anime, Movie/TV or release acceptance. The [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) records completed implementation steps separately from those open gates.

## September 28 watchability audit and product amendments

The user's latest priorities require anime to be watchable before Movies, then Television, followed by books (Anna's Archive preferred); Cartoons are last and mean primarily old Arabic-dubbed anime. The [watchability audit](C:/Users/user/animeapp/docs/qa/watchability-audit-2026-09-28.md) now specifies affected code, implementation batches, actual failure evidence and remaining package mapping. The [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) remains the only progress checklist. This amendment supersedes older presentation/provider priorities where they conflict; dated checkpoints below remain historical evidence.

**Confirmed on the unchanged tested build:** returning to a native anime tab produced a white surface while LibVLC time advanced. Separate player view models already exist, but the active content host/unload path does not retain per-tab native/WebView views and selection does not pause outgoing playback. A production `audio=dub` probe skipped initial autoplay, then accepted Stage D's default stream; parsed MPEG-TS PMT identified Japanese audio. Native caption handoff is absent on the demonstrated Sub route. Controls still reserve a bottom layout row and duplicate site controls. These findings leave full playback acceptance open despite the preceding 457-passed/23-Python baseline; no new production edit/build was made by this audit.

**Implementation order:** A, retain each open player view/surface/document and pause outgoing tabs; B, native overlay/fullscreen/idle controls and complete site-panel hiding; C, real native subtitle handoff and verified actual Dub audio in every extraction path; D, exact matching, measured lookup improvements and the full anime gate. Then prove Movies and TV through real playable sources, actual tracks and resume. Use the existing progressive source contracts rather than leaving the UI on the legacy collector. Keep the non-TMDB/keyless default catalog and automatic EverythingMoe/The Index discovery.

**Temporary-download exploration:** the user selected Nyaa-style torrents and retention until tab close. Existing RSS and MonoTorrent 3.0.2 support a small metadata-first, one-episode-file prototype. The current season queue/first-completed-file path is not that implementation. Preserve correct episode/audio matching, selected-file completion, bounded progress/cancellation/disk use and leases until the final owning tab closes; release handles before deleting only owned temporary cache data. Keep streaming as the current default while evaluating the opt-in prototype. A default replacement and Cartoon removal remain undecided.

**Presentation requirement:** site/WebView mode must hide the whole duplicate app playback panel in normal/fullscreen/PiP, use the site's controls and retain app exit/recovery through window/tab actions. Native controls sit over video in the supported WPF video overlay host, show on interaction and hide on idle without resizing the video. Multiple inactive players pause and remain ready to resume. UI geometry or a progressing timeline alone is not evidence of a rendered video.

The audit expanded the previously combined IP12–IP18 entry so transfer, progressive results, images, persistence, settings, journeys and packaging remain visible. Apply those requirements to concrete defects and acceptance checks; do not build additional abstractions merely to complete a work-package label. It assigned batch A first; the twenty-second checkpoint above and the active checklist now record the subsequent implementation and remaining acceptance.

## Twenty-first implementation checkpoint - false direct-stream rejection

**September 28, 2026: IP11/IP17 focused playback repair.** Investigating why automatic playback opened the site reproduced a valid HLS manifest whose video segments were labeled `image/jpeg`. Bounded byte inspection confirmed MPEG-TS packet headers. Python validation and the HLS proxy both rejected the MIME label before inspecting the payload, turning usable video into a browser fallback. Verify consecutive transport-stream headers before accepting an image-labeled segment, replay the proxy's bounded inspection prefix and retain real image/HTML/JSON rejection. Correct the routing log so it no longer claims all native candidates were exhausted.

**Verification:** zero build warnings/errors; **457 passed, 22 existing skips, zero failures**, plus **23 Python cases passed**. Seven new cases in each language cover actual TS, unaligned ranges and decoy rejection. The same-page live extractor changed from `requires_webview: true` to `false`. The exact new build resolved the selected episode through Tier 1 native HLS, restored 535.8 seconds, displayed advancing video, paused at 09:43 and sought forward 30 seconds. [QA notes](C:/Users/user/animeapp/docs/qa/direct-stream-2026-09-28.md), [TRX](C:/Users/user/animeapp/.artifacts/implementation/results/direct-stream.trx), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/direct-stream-checkpoint.json).

**Remaining:** the native player showed Japanese audio metadata but no caption track; CC was disabled while the previous browser flow rendered subtitles. Complete caption handoff and verify Sub/Dub before accepting native anime playback. Lookup still took about 43 seconds. Exact title/unit/audio evidence, transfer lifetime, broader provider compatibility, changed-provider resume and release acceptance remain open. No keys, provider domain adapters or storage changes were added.

## Twentieth implementation checkpoint - audio handoff, iframe controls and presentation repair

**September 17–28, 2026: IP11/IP17 focused playback repair.** Preserve requested Sub/Dub in the episode context and use labelled site audio controls at the browser handoff. Subscribe to nested WebView2 frame events, bind each document to its navigation/origin/token, invalidate old documents and route controls/resume to the selected video frame. The app timeline now follows iframe progress, and Play waits for video confirmation. Browser-policy rejection shows a page-Play instruction. Browser time updates no longer seek the inactive native player.

**Live evidence:** requested Dub was visibly selected on the provider page for Mushoku Season 3 / episode 1. Video advanced with matching app time; app pause, resume and +30s seek worked. Closing saved 378.37322 seconds in SQLite. The [QA notes](C:/Users/user/animeapp/docs/qa/iframe-playback-2026-09-17.md) record the separate fresh-browser restore check so provider-site resume cannot be mistaken for app resume.

**Verification:** final build zero warnings/errors; **450 passed, 22 existing skips, zero failures**, including nine audio/frame cases and two presentation cases. Python is unchanged from the prior checkpoint. [Final TRX](C:/Users/user/animeapp/.artifacts/implementation/results/iframe-playback-final.trx), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/iframe-playback-checkpoint.json). No new key requirement, provider picker, hostname-specific adapter or storage migration.

**Presentation repair:** live testing exposed both browser disposal during temporary WindowChrome unloads and a zero-size WebView graphics surface when shrinking to PiP. Resolve the actual content row, defer true-unload cleanup until after Loaded, remove navigation before shrinking and restore it after expanding, and retain a nonzero browser surface. Browser PiP uses the site controls plus Exit PiP to leave room for video. The QA notes retain the failed live run, the passing resize retry and final verification.

**September 28 exact-build live result:** automatic Sub lookup reached the correct Season 3 episode after 42 seconds. The app restored 452.1 seconds in a video iframe. The compact browser PiP showed the playing video and Exit PiP; leaving PiP restored the controls and continued playback. A separate app Pause click stopped both page and app at 08:55. The tested app remains paused in the isolated profile. Browser fullscreen with active video passed on the immediately preceding resize build. This closes the named browser presentation repair, while whole playback acceptance remains open.

**Remaining:** independent exact title/unit/audio evidence, broad provider/frame compatibility, cross-provider/cut resume, source-search latency and whole playback/release acceptance. The first live source resolution still took about 45 seconds.

## Nineteenth implementation checkpoint - browser startup and interaction

**September 15, 2026: IP11/IP17 focused repair.** The user challenged the new 30-second browser timeout because the same sources previously loaded slowly and eventually played. A live trace separated roughly 45 seconds of automatic source discovery from three seconds of browser startup. The recovery overlay intercepted provider input and declared failure without terminating navigation. Removing that overlay and keeping the browser available let the provider video advance to 55 seconds and pause after the old timeout.

Browser status/errors now appear below the page; root input preserves browser focus. Browser controls stay visible to avoid moving provider buttons when the pointer enters. Same-URL Retry requests navigation after resetting state and reuses the initialized browser environment. The native overlay template explicitly hides in browser mode. Native startup failure behavior remains. No new provider configuration, keys, metadata migration or Python changes were introduced.

**Verification:** `browser-startup-verified-build` compiled with zero warnings/errors; **439 passed, 22 existing skips, zero failures**. Seven added cases cover late playback, stale startup state, native timeout preservation, real WPF hit testing and same-URL retry. [QA evidence](C:/Users/user/animeapp/docs/qa/browser-startup-2026-09-15.md), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/browser-startup-checkpoint.json), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/results/browser-startup-verified.trx).

**Remaining:** the provider video runs in an iframe while app telemetry/commands target the outer page. The app's timeline/status/resume therefore do not establish that iframe's progress. The live provider audio selector showed Sub despite the app requesting Dub. Correct frame event/command routing with source/session validation, audio handoff and source-search latency are the next focused work. This checkpoint does not close whole playback or release acceptance.

## Eighteenth implementation checkpoint - automatic anime indexes and live player repair

**September 15, 2026: IP07/IP11/IP17 focused recovery.** Removed the anime static provider dropdown/editor and seed domains in accordance with the user's correction. Normal Sub/Dub playback uses EverythingMoe and The Index, with index-attributed cache fallback, replacement of changed domains and automatic rotation. Existing saved custom URLs are preserved; separate dub-badge and non-anime provider dependencies remain open.

Live testing exposed a wrong-title recommendation accepted after rejection by the scorer; route bonuses can no longer revive rejected candidates. Movie desktop checks passed for Dune remakes, paging, details and explicit source search. Native video showed the David Lynch credit, advanced, sought forward and paused. Player-tab closure then crashed on a queued VLC audio-track query after disposal. Callback and track-refresh guards address that specific defect; final live closure/resume results are in the QA notes.

**Verification:** corrected `player-close-build` compiled with zero warnings/errors; **432 passed, 22 existing skips, zero failures**. **16 Python tests passed**. Five new automatic-routing/configuration cases, three lifecycle cases, ten Python index cases and six candidate-matching cases were added in this batch. The live index returned 203 entries, not 203 proven streams. [QA evidence](C:/Users/user/animeapp/docs/qa/index-discovery-2026-09-15.md), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/index-discovery-checkpoint.json), [final TRX](C:/Users/user/animeapp/.artifacts/implementation/results/player-close-e2e.trx).

**Remaining:** exact anime season/part and audio evidence, complete native/browser/TV playback and resume acceptance, movie source latency and truthful source labels, broad catalog coverage, Cartoon routing and packaging. No whole package is marked accepted by this checkpoint.

## Seventeenth implementation checkpoint - replace general uploads in Movies

**September 13, 2026: IP05/IP06 focused Movie recovery.** Live inspection reproduced miscellaneous Archive uploads in Movies. Movie discovery now selects typed Wikidata films, with optional exact-ID IMDb posters, independently of saved TMDB keys and the Archive source toggle. Feature details require an explicit Find sources action. Existing paging, identities, request coordination and library behavior are reused.

Live production-HTTP checks caught an interactive maxlag rejection missed by an earlier probe; this was fixed without disabling HTTP rate limits. A Commons poster failure led to preferring validated IMDb artwork while retaining explicit Wikidata fallback. MovieService returned separate Dune remakes and distinct browse pages. Real isolated app startup reached required service readiness in about three seconds; grouped navigation, Settings and populated anime results were observed.

**Verification:** final build zero warnings/errors; **424 passed, 22 existing skips, zero failures**. Final-source artwork probe returned the corrected poster with HTTP 200. [QA evidence and limits](C:/Users/user/animeapp/docs/qa/film-catalog-2026-09-13.md), [source/build manifest](C:/Users/user/animeapp/.artifacts/implementation/film-catalog-checkpoint.json) and [test results](C:/Users/user/animeapp/.artifacts/implementation/results/film-catalog-e2e.trx).

**Remaining:** final Movie desktop/playback flow was interrupted by manual input/minimization and is unverified. Five-item pages, several-second searches, incomplete artwork/classification and the first-page `Your Name` miss remain open, as do Cartoon routing, source evidence and playback/resume acceptance. See the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) for the next concrete work; no package acceptance is closed by this checkpoint.

## Sixteenth implementation checkpoint - keyless TV catalog and episode selection

**September 13, 2026: IP05/IP06 Television default and episode UI.** TVmaze is now the default for TV browsing, search and episode metadata, independent of a saved TMDB key or the Archive source toggle. Per-catalog capabilities stay accurate; saved TVmaze series IDs retain their episode provider. Movie and Cartoon discovery remain on their transitional routes while film qualification continues.

Series details load season/episode choices without automatically starting source scrapers. Selecting an episode forwards its actual numbered unit; switching titles/seasons, closing details or disposing cancels/invalidates previous work. Unnumbered specials remain visible without becoming guessed playback units. Unavailable or unsupported episode lists expose manual number entry and retry. The TV catalog/details include attribution links, plain-text summaries and ratings; Settings explains which catalogs use each provider.

The broader keyless live sample passed twice: six searches, two index pages with 240/245 shows and full poster coverage, and 203 Office episodes. Cinemeta returned TMDB-related metadata fields and is not selected as a clean non-TMDB film replacement. See the updated [provider decision](C:/Users/user/animeapp/docs/decisions/keyless-metadata.md).

**Verification:** full exact-build suite **410 passed, 22 existing skips, zero failures**, with zero build warnings/errors. Twenty added cases cover default routing, saved-key behavior, provider failures, identity-owned episodes, presentation fields, episode-selection lifetime and actual view bindings/layout at 1280 dark and 900 light. [QA notes](C:/Users/user/animeapp/docs/qa/tvmaze-default-2026-09-13.md) record the limits and evidence. Results: `.artifacts/implementation/results/tvmaze-default-e2e.trx`; source/build hashes: `.artifacts/implementation/tvmaze-default-checkpoint.json`.

**Remaining:** qualified film/animated-film discovery, Cartoon's combined catalog strategy, exact specials/alternate-order mapping, independent source evidence, live playback and full desktop/DPI acceptance. Existing legacy series without a verified TVmaze ID keep their saved identity and use manual episode entry; no title-based ID conversion occurs. Build: `.artifacts/implementation/tvmaze-default-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`.

## Fifteenth implementation checkpoint - versioned audiovisual library

**September 13, 2026: IP08 library migration and reconstruction.** Added identity-based library keys, a version-2 envelope, provider IDs, frozen keys, aliases/categories and retained original records. Catalog and My List share reconstruction and preserve keyless IDs and persisted opaque keys. New enrichment can reuse a saved work through a shared nonconflicting provider ID.

Conversion validates first, backs up the exact original bytes, stages a complete file and replaces atomically. Memory changes and events publish only after persistence succeeds. Corrupt/future files cannot become an empty overwrite. Equivalent legacy Movie/Cartoon records merge only through provider identity and known form; favorites, latest display/status/opening, category aliases and original records survive. Ambiguous or conflicting entries remain separate.

**Verification:** clean build with zero warnings/errors; full exact-build suite **390 passed, 22 existing skips, zero failures**. Eleven new migration cases cover backups, idempotence, equivalence/conflicts, opaque records, provider-ID case, enrichment, failed commits, invalid/future files and cancellation. Evidence: .artifacts/implementation/results/library-migration-e2e.trx, .artifacts/implementation/library-migration-checkpoint.json and [migration QA notes](C:/Users/user/animeapp/docs/qa/library-migration-2026-09-13.md).

**Remaining:** reconcile multiple already-canonical entries, verified legacy SQLite imports, synchronize playback summaries, coordinate separate writers and complete real migrated-profile/rollback journeys. SQLite rows were not converted. Final keyless non-TMDB provider selection remains open. Build: .artifacts/implementation/library-migration-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe.

## Fourteenth implementation checkpoint - canonical audiovisual resume keys

**September 13, 2026: IP08, authoritative resume step.** Native and embedded audiovisual playback now use the captured WorkKey/UnitKey for SQLite resume reads and writes. Features survive URL/title changes, numbered episodes remain separate across seasons, and separate provider identities do not merge by title. Unresolved audiovisual units skip resume instead of falling back to guessed title/URL keys. Existing anime behavior and legacy SQLite rows are preserved.

Accepted writes are sequenced per work/unit within each player. Older pending writes cannot overwrite a newer completion, and post-completion progress is excluded. This does not provide cross-player serialization or migrate the JSON library.

**Verification:** clean build, zero warnings/errors; full exact-build suite **379 passed, 22 existing skips, zero failures**. Six added cases verify URL/title/player changes, seasons and same-title works, unresolved-unit legacy preservation and reordered completion writes. Evidence: .artifacts/implementation/results/canonical-resume-e2e.trx, .artifacts/implementation/canonical-resume-checkpoint.json and [resume QA notes](C:/Users/user/animeapp/docs/qa/canonical-resume-2026-09-13.md).

**Remaining:** IP08 versioned JSON migration, verified aliases/import, shared library reconstruction and playback summary updates; IP15 cross-player and shutdown persistence guarantees. Old ambiguous title/URL positions are retained but not imported. Final non-TMDB keyless provider selection and live playback acceptance remain open. Build: .artifacts/implementation/canonical-resume-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe.

## Thirteenth implementation checkpoint - frozen playback context

**September 13, 2026: IP04 context contract and forwarding.** Catalog cards retain their work key; playback captures identity/unit, title/poster, provider ID, source identity and evidence in an immutable context. Alias/title/audio/subtitle lists are defensively copied. Transport credentials and URLs remain outside the identity context. Unknown forms and unestablished special numbering retain a null unit key.

The optional context is appended to existing playback signatures and forwarded through the message, main view model, native/embedded loads, retry, browser fallback and quality switching. Unrelated loads clear it. Existing anime calls remain compatible.

**Verification:** zero build warnings/errors; full exact-build suite **373 passed, 22 existing skips, zero failures**. Seven new regression cases cover snapshot ownership, special/unknown units, transport separation, message compatibility, retry, native load, browser fallback and reset. Main/quality forwarding compiled and inspected. Evidence: .artifacts/implementation/results/playback-context-e2e.trx, .artifacts/implementation/playback-context-checkpoint.json and [playback-context QA notes](C:/Users/user/animeapp/docs/qa/playback-context-2026-09-13.md).

**Remaining:** independent producer evidence and exact matching, provider episode identities for unnumbered specials, IP08 canonical persistence/resume adoption and live playback journeys. Capturing evidence does not establish that it is verified. Final keyless non-TMDB provider selection remains open. Build: .artifacts/implementation/playback-context-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe.

## Twelfth implementation checkpoint - source selection lifetime

**September 13, 2026: IP04/IP07, partial source-selection step.** Changing title, season, episode, language or Arabic-only selection invalidates the old source list and cancels its lookup. Late responses and failures cannot overwrite newer results. Play, external-open and download reject stale source objects; playback/external-open recheck selection after network validation. Delayed library reads and superseded detail opening cannot update another card.

**Verification:** clean build with zero warnings/errors; full exact-build suite **366 passed, 22 existing skips, zero failures**. Nine added regression cases cover selection changes, late success/failure, close/reopen, retained button objects, current-source validation and disposal. Evidence: .artifacts/implementation/results/source-selection-e2e.trx, .artifacts/implementation/source-selection-checkpoint.json and [source-selection QA notes](C:/Users/user/animeapp/docs/qa/source-selection-2026-09-13.md).

**Remaining:** full frozen playback context, independent source evidence, exact specials, persisted resume migration and real playback acceptance. IP05 provider coverage/artwork qualification and the final keyless non-TMDB routing decision remain open. This step fixes stale UI selection, not provider identity accuracy. The new isolated build is .artifacts/implementation/source-selection-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe.

## Eleventh implementation checkpoint - dependency health and repair

**September 11, 2026: IP09/IP16, dependency step.** Service health now refreshes after individual dependency checks and distinguishes pending/preparing from unavailable. One Repair services action prepares Python, verifies FFmpeg and retries uBlock. FFmpeg requires successful bounded version checks from both executables; file presence alone is insufficient. uBlock reports Installed rather than unverified browser loading.

Dependency runs and extension repairs are serialized per bootstrapper instance. Extension activation retries transient rename failures up to three times, reports the failed stage, and retains pinned version/digest checks, rollback and owned staging cleanup. The request budget increased from eight to 20 seconds. Historical staging access denial was not reproduced and is not declared resolved.

**Verification:** clean build, zero warnings/errors; full exact-build suite **357 passed, 22 existing skips, zero failures**. Ten added tests cover executable validation, PATH selection, timeout, health publication, bounded rename retries and concurrent repair. An isolated real bootstrapper probe successfully ran local ffmpeg/ffprobe and installed the official pinned uBlock archive. An existing rapid-search UI test was corrected to accept the command disabling its button during execution; the initial failing result is retained. Evidence: `.artifacts/implementation/results/dependency-e2e.trx`, `.artifacts/implementation/dependency-checkpoint.json` and [dependency QA notes](C:/Users/user/animeapp/docs/qa/dependency-health-2026-09-11.md).

**Remaining:** actual desktop Repair services and WebView2 extension loading acceptance, historical permanent permission diagnosis if it recurs, remaining IP09 protocol/revision/shutdown work, and the open metadata/matching/resume packages. No new keys were introduced; this checkpoint does not select the final non-TMDB provider. Build: `.artifacts/implementation/dependency-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`. The existing manual app was not replaced.

## Tenth implementation checkpoint - shared Python preparation

**September 10, 2026: IP09, preparation step.** Replaced file-existence readiness with shared NotStarted/Preparing/Ready/Failed/Canceled snapshots. One preparation operation owns its two-minute deadline; individual callers can cancel waiting independently. Preparation resolves Python 3 asynchronously, deploys scripts, probes actual imports, installs only missing packages, rechecks imports and validates syntax. Success is cached against source-script hashes; failed attempts can recover through retry.

Added a bounded process runner that drains stdout/stderr concurrently, caps retained output, terminates owned process trees on cancellation and awaits bounded cleanup. Scraper engines preserve structured preparation failure snapshots. Settings/status health uses the shared state and exposes Repair Python. Startup and Settings agree on enabled-by-default policy; Python preparation no longer waits behind extension setup.

**Verification:** zero build warnings/errors; full exact-build suite **347 passed, 22 existing skips, zero failures**. Fourteen added cases cover default policy, shared work, canceled waiters, false-ready prevention, retry, revision changes, missing scripts, budget expiry, explicit cancellation, saturated process pipes and real owned-process termination. A local import/syntax probe passed for the required modules and all three scripts. Evidence: `.artifacts/implementation/results/preparation-e2e.trx`, `.artifacts/implementation/preparation-checkpoint.json` and [preparation QA notes](C:/Users/user/animeapp/docs/qa/python-preparation-2026-09-10.md).

**Remaining:** runtime/package revision invalidation, full scraper CLI protocol checks, complete app-shutdown await evidence and live startup/repair acceptance. FFmpeg availability and uBlock staging remain separate unresolved issues. This checkpoint does not assert that all services in the previously launched profile are repaired. Latest build: `.artifacts/implementation/preparation-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`; the existing manual app was not replaced.

## Ninth implementation checkpoint - catalog paging UI

**September 10, 2026: IP06, provider-independent UI step.** Connected catalog screens to `IPagedAudiovisualCatalogService`, with request snapshots, `HasMore`/`IsLoadingMore`, a Load more control, real continuation and typed provider-state messages. Each completed page updates the card collection in one batch. Later-page failure preserves existing cards and the retry cursor; filtered empty pages can continue. Failed requests are not presented as successful empty searches.

Catalog requests now have a cancellation lifetime separate from source resolution and downloads. Query/filter/library changes invalidate the old catalog sequence; disposed or superseded requests cannot update the catalog. In-memory card deduplication and selection use provider identity and content form, preserving different non-TMDB IDs with the same title/year. Unknown identities are not merged by title. Persisted favorites/resume still use the legacy library format until IP08.

**Verification:** build passed with zero warnings/errors; full exact-build suite **333 passed, 22 existing skips, zero failures**. Six new tests cover append/deduplication, retry preservation, late uncooperative responses, filtered-empty/provider-error states, disposal/library switching and distinct keyless selection. Evidence: `.artifacts/implementation/results/catalog-ui-e2e.trx` and `.artifacts/implementation/catalog-ui-checkpoint.json`. Reproduce with `.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/catalog-ui-build`.

**Remaining:** final non-TMDB provider selection/configuration migration, season/episode controls, attribution and actual desktop Load more interaction/visual acceptance. This batch implements UI work independent of the unresolved IP05 provider gate; it does not change the default provider or satisfy useful movie catalog acceptance. No new API keys were introduced.

## Eighth implementation checkpoint - series index and episode metadata

**September 10, 2026: IP04/IP05, series step.** Added the public episode metadata interface and typed episode results. Metadata routing and all three audiovisual catalog services forward the API; providers without support return Unsupported. TVmaze supplies season-filtered episode lists, distinct provider episode IDs and specials without inventing playback numbering. A captured response contains 202 regular episodes and one unnumbered special.

TVmaze index browsing now slices up to 250 upstream entries, preserves short-page continuation and terminates only on index 404. A typed NotFound outcome prevents missing resources from triggering provider-wide outage backoff. Sparse cartoon browsing refills at most three upstream pages per call, retaining continuation when its budget is exhausted.

**Verification:** clean build with zero warnings/errors; full exact-build suite **327 passed, 22 existing skips, zero failures**. Eleven added cases cover the episode/index contracts and HTTP 404 status. Evidence: `.artifacts/implementation/results/episodes-e2e.trx`, `.artifacts/implementation/episodes-checkpoint.json`, and the attributed trimmed episode fixture. Reproduce with `.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/episodes-build`.

**Remaining:** final provider selection, artwork and broader coverage/availability qualification; catalog UI adoption; frozen playback context and exact special playback mapping. The adapters remain outside default production selection, so the current visible catalog has not switched providers. Ordinary movie/TV/cartoon use must remain keyless and non-TMDB in the target implementation.

## Seventh implementation checkpoint - typed keyless film metadata

**September 10, 2026: IP05, film prototype step.** Added `WikidataMetadataClient` through the IP04 metadata contract. Typed search preserves both Dune remakes; direct film and animated-film classes remain distinct from non-film entities. English/shared-language label handling restores Toy Story's title. Real upstream continuation and one bounded entity batch per page support discovery beyond the first results. Pages request at most five entities after a 20-entity live probe exceeded the response budget.

Ten new deterministic cases cover captured remake/browse/animation results, shared-language labels, batch enrichment, API errors, failed-page handling, cancellation, token binding and rejection of deprecated classification/artwork claims. No TMDB IDs or playback-language claims are manufactured. See [the expanded decision record](C:/Users/user/animeapp/docs/decisions/keyless-metadata.md).

**Verification:** build passed with zero warnings/errors; exact-build suite **316 passed, 22 existing skips, zero failures**. Evidence: `.artifacts/implementation/results/wikidata-e2e.trx`, `.artifacts/implementation/wikidata-checkpoint.json`, and `.artifacts/implementation/metadata-qualification/typed-film-sample.json`. Reproduce with `.\run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/wikidata-build`.

**Remaining:** the adapter is not selected in production routing. Artwork, broader subclass/partial-search coverage, browse relevance/latency and provider availability remain qualification gates. One repeated live request returned 403. TVmaze index/episode integration and the final non-TMDB provider decision remain open. This checkpoint improves the replacement implementation; it does not yet change the visible catalog.

## Sixth implementation checkpoint - non-TMDB provider qualification

**September 10, 2026: IP05, first qualification step.** Compared IMDb, TVmaze and Wikidata in [the provider decision record](C:/Users/user/animeapp/docs/decisions/keyless-metadata.md). Inspection confirmed that the legacy IMDb browse method still scrapes TMDB; it cannot meet the replacement requirement. A bounded, repeatable live probe now covers ordinary films, sequels, remake coverage, animated films/series, series index pages and episodes without credentials.

Implemented a TVmaze search qualification adapter using the IP04 metadata interface and shared bounded request coordinator. It preserves remake IDs/year distinctions, verifies animation classification, slices search results with request-bound continuation and reports unsupported film/discovery/locale requests honestly. It does not infer playback audio from show language. Two trimmed upstream fixtures and eight tests cover paging/cache reuse, remakes, animation, cancellation, invalid payloads and rate limits.

**Verification:** clean build; full exact-build suite **306 passed, 22 existing skips, zero failures**. Evidence: `.artifacts/implementation/metadata-build/results/recovery-e2e.trx`, `.artifacts/implementation/metadata-qualification/qualification-sample.json`, and `.artifacts/implementation/metadata-checkpoint.json`.

**Remaining:** IP05 is not complete. TVmaze is series-scoped and the prototype is not selected in production. Wikidata's initial film sample exposed remake-search, label and artwork gaps; useful browse and deeper typed search must pass before provider selection. IMDb dataset feasibility remains an alternative. Complete the provider decision, episode/special API and routing migration before claiming the visible movie catalog is repaired. No new keys or production routing changes were introduced.

## Fifth implementation checkpoint - source evidence and updates

**September 10, 2026: IP04, source contract step.** Added source search requests, independent identity/unit/audio/subtitle evidence, tri-state verification and candidate/verification/ready/failure/completion updates. Added optional evidence to the scraper DTO and source model; old JSON remains readable and missing evidence stays unverified. The scraper adapter carries supplied evidence without constructing it from the selected title or language.

The new verifier checks provider-ID conflicts, content form, exact title/year when IDs are unavailable, animation evidence, exact episode numbers and established specials. Audio and subtitles are separate: requested dub language, original-language metadata and subtitles cannot establish audio. Verified keyless title/year/form evidence is supported. No new API keys or credential requirements were added.

Movie/TV/Cartoon services now expose `FindSourceUpdatesAsync`. Completed provider groups can publish before slower groups; failures are isolated and completion includes candidate/ready/unverified/rejected/failure counts. The new path emits `SourceReady` only after evidence verification and supported transport checks; it retains the stricter Arabic-cartoon transport verifier. Candidate IDs hash provider/location for operation tracking rather than exposing replay URLs. Cancellation and disposal cancel pending cooperative provider tasks and observe their completion.

**Verification:** build passed with zero warnings/errors; full exact-build suite **298 passed, 22 existing skips, zero failures** (320 total). Twelve new cases cover old/new payloads, request echoes, ID conflicts, keyless matching, audio versus subtitles, episodes/specials, fast-provider delivery, early disposal/cancellation, completion counts, secret-free failures, Arabic transport verification and public service exposure.

**Evidence/build:** `.artifacts/implementation/results/source-evidence-e2e.trx`, `.artifacts/implementation/source-evidence-checkpoint.json`, and `.artifacts/implementation/evidence-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`. Reproduce with `./run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/evidence-build`.

**Limitations:** current Python/manifest/Archive producers still need independent evidence extraction (IP07); until they supply it, their results on the new API remain unverified. Existing list/boolean matching and current UI behavior are retained until migration. Incremental delivery is between provider groups; per-candidate scheduling, process ownership/deadlines and legacy swallowed failures remain IP09/IP10. Explicit provider selection on the new API currently supports Archive; unsupported selections are reported without falling back to other providers. Episode metadata and playback context are the remaining IP04 contracts. Live movie correctness is not established by this checkpoint.

## Fourth implementation checkpoint - paged metadata contracts

**September 10, 2026: IP04, paging step.** Extracted `IAudiovisualMetadataClient` into its own file and added catalog requests/pages, capability flags and `IPagedAudiovisualCatalogService`. Movie, TV and Cartoon services forward page requests through the production metadata router. TMDB and Archive list methods now collect the first page from their real paged implementations. The legacy IMDb suggestion adapter explicitly reports `Unsupported` for paging; absent metadata configuration reports `NotConfigured`.

Continuation tokens are authenticated and scoped to the app run. They bind provider/configuration, mode, normalized query, kind, effective locale and requested page size, carrying upstream page/offset without embedding API keys or search text. Invalid, changed-provider and tampered continuations fail before a request is sent. Smaller requested pages preserve leftover upstream items; filtered-empty Archive pages retain continuation when upstream records remain. A failed later response is separate from earlier page data. Tokens must be discarded after app restart; they do not represent a persistent upstream snapshot.

TMDB results now retain a namespaced primary ID and animation evidence, and deduplicate by content form plus ID instead of ID alone. This keeps an animated film and series with the same numeric ID separate. Archive retains its case-sensitive item identifier. Archive's existing classification/search limitations are unchanged and remain under IP05/IP06.

**Verification:** build completed with zero warnings/errors; full exact-build suite **286 passed, 22 existing skips, zero failures** (308 total). Nine new paging cases cover slicing/collision handling, request binding/tampering, filtered-empty continuation, malformed versus empty responses, later-page failures, public service routing/provider changes and unsupported/cancelled requests.

**Evidence and build:** `.artifacts/implementation/results/paging-e2e.trx`, `.artifacts/implementation/paging-checkpoint.json`, and `.artifacts/implementation/paging-build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`. This separate build directory preserves the already-running manual test executable. Reproduce with `./run-e2e-tests.ps1 -ArtifactsPath .artifacts/implementation/paging-build`.

**Remaining:** source update/evidence contracts, episode metadata API and playback context (IP04), metadata qualification (IP05), and adoption of paging/outcomes in catalog screens (IP06). The current screens still call first-page compatibility methods. AniList/uBlock issues remain deferred at the user's request.

## Third implementation checkpoint - audiovisual identity and provider outcomes

**September 10, 2026: IP04, first contract step.** Added `AudiovisualExternalId`, optional `PrimaryId`/`ExternalIds`/animation evidence on `AudiovisualIdentity`, and `AudiovisualIdentityKeys`. Helpers validate provider namespaces, retain case-sensitive provider item values, normalize numeric TMDB IDs, reject conflicting IDs/forms, preserve a supplied persisted work key, and require exact episode numbering (including explicit evidence for season-zero specials). Unknown content form does not acquire a TMDB movie namespace just because the item is displayed in Movies. Unidentified records receive opaque local keys instead of title-based merges.

Added `ProviderOutcome`, `ProviderFetchResult` and typed statuses. `ProviderRequestCoordinator.FetchAsync` reports successful empty responses, disabled/missing configuration, disallowed destinations, invalid credentials, rate limits, service failures, timeouts and oversized responses separately. Results include elapsed time, retry timing and cache metadata. Diagnostics use fixed codes and safe provider identifiers. The existing `GetStringAsync` now delegates to the new method as a compatibility adapter, so production requests exercise the same implementation while existing callers retain their behavior. Existing host restrictions, response bounds, cache limits, throttling and caller cancellation remain in force.

**Verification:** build passed with zero warnings/errors; full exact-build suite **277 passed, 22 existing skips, zero failures** (299 total). Nineteen new cases cover old/new JSON, namespace collisions, case-sensitive IDs, frozen-key reuse, episode identity, HTTP status distinctions, warm-cache cancellation, backoff, response bounds, secret-free diagnostics and cancellation/deadlines after response headers.

**Evidence:** `.artifacts/implementation/contracts-checkpoint.json` and `.artifacts/implementation/results/contracts-e2e.trx`.

**Not yet complete:** the identity helpers are additive contracts, not a library migration or replacement of the existing matcher. Catalog paging/capabilities, typed source updates/evidence, UI outcome handling, metadata qualification and actual playback/resume wiring remain pending. This checkpoint does not establish that live movies work. Next IP04 work is the real paged metadata interfaces/adapters and continuation validation, followed by source-evidence contracts and integration.

## Second implementation checkpoint - shared controls and tab chrome

**September 10, 2026:** continued IP02 and the tab-action portion of IP03. Added explicit themed tab action states, stable automation IDs, density-aware compact metrics, and an outer dropdown selection presenter. Shared button, navigation, input-focus and system-text colors now respond to the active palette. Existing commands, selected values and view-model ownership are retained.

Added `ControlThemeRenderTests` covering real compiled WPF resources, enabled/disabled tab chrome, rendered selected labels, `SelectedValuePath`, palette switching, and contrast for all seven supported accent choices. The rendering helper runs on STA and writes reviewable PNGs.

**Evidence:** [dark controls](C:/Users/user/animeapp/.artifacts/implementation/build/bin/UniversalMediaOS.Tests.E2E/debug/ui-evidence/controls-dark.png), [light controls](C:/Users/user/animeapp/.artifacts/implementation/build/bin/UniversalMediaOS.Tests.E2E/debug/ui-evidence/controls-light.png). Both images were inspected. Normal and disabled tab controls use the intended transparent chrome; selected text and action labels are readable in both themes.

**Verification:** full exact-build suite **258 passed, 22 existing skips, zero failures** (280 total). Build completed with zero warnings and errors. Results: `.artifacts/implementation/build/results/recovery-e2e.trx`.

**Checklist:** the [separate active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md) records both batches as individually checked implementation steps, with unfinished package and release acceptance listed separately. Keyboard/popup interaction, full interactive-state rendering, accessibility and real monitor/DPI tests are still open. Movie fixes remain planned under IP04-IP10.

## Verified implementation checkpoint - September 10, 2026

The first recovery changes are implemented in the working tree. This is not a release or completion of the full plan.

| Package | Implemented | Still required |
|---|---|---|
| IP00 | Created `codex/app-recovery`; preserved 213 source files in a SHA256-verified ZIP; built into a separate artifacts directory; runner explicitly selects the executable and test DLL. | Release artifact verification remains IP18. |
| IP01 | Added `UNIVERSAL_MEDIA_OS_DATA_ROOT`, preserving ordinary default paths. Core/WPF app-owned local and roaming storage, Python cache/profile paths, and WebView2 profiles honor it. Test fixture restores the environment on failure/disposal. Production DI accepts test overrides. Added STA layout checks. | Expanded service fixtures, operation diagnostics and timing probes. Explicit user-configured external paths still take precedence over defaults. |
| IP02 | Removed inherited 220-pixel dropdown minimum; replaced nested native toggle chrome; forwarded the selected-label template selector; added compact playback dropdown styling with actual 72/88 by 34 layout checks. | Live keyboard/open-popup, interactive state and accessibility matrix. Shared/tab styling and dark/light render evidence added in the second checkpoint. |
| IP03 | Media and utility commands are separate groups; utilities align to the trailing edge and move as one group to a second row. Existing commands and tab logic remain wired. Wide/narrow geometry and live navigation tests pass. | Full RTL, localization, DPI and window-drag matrix and visual sign-off. |
| IP11 | Automatic anime discovery no longer repeats the exhausted crawl. Manual browser discovery remains available; saved browser fallback is reused. Cancellation and failure regression tests pass through an injectable resolver interface. | Live timing and automatic index Sub/Dub playback matrix (static provider selection superseded by the September 15 user correction). |

**Verification:** SDK build passed with zero warnings/errors. Full exact-build test run: **254 passed, 22 existing skips, zero failures** (276 total). Both modified Python files passed AST syntax validation. Startup/navigation also passed in the isolated application fixture. These checks do not establish working live movies or full performance recovery.

**Artifacts:**

- Source backup: `.artifacts/implementation/baseline-20260909-233414/source.zip` and `manifest.json`.
- Test result: `.artifacts/implementation/build/results/recovery-e2e.trx`.
- Executable: `.artifacts/implementation/build/bin/UniversalMediaOS.WPF/debug/UniversalMediaOS.WPF.exe`.
- Default isolated storage layout: `<UNIVERSAL_MEDIA_OS_DATA_ROOT>/Roaming/UniversalMediaOS` and `<UNIVERSAL_MEDIA_OS_DATA_ROOT>/Local/UniversalMediaOS`. An override must be absolute. Portable legacy SQLite import is disabled under the override to avoid pulling real user data into tests.

Choose `./run-e2e-tests.ps1 -Filter '<affected test selection>'` for focused checks or explicitly use `-FullSuite` for a full checkpoint. Older reproduction commands above that omit a scope now need `-FullSuite`. Add `-NoBuild` only when reusing a current artifact; reuse the existing build root. `-ArtifactsPath` selects that build root; `-ExecutablePath` (or `UNIVERSAL_MEDIA_OS_EXE`) selects an explicit application executable.

**Next integration work:** finish IP04 episode metadata/playback context, prioritize credential-free metadata qualification (IP05), and implement independent producer evidence plus UI adoption (IP06-IP08/IP10). Remaining UI acceptance and IP01 diagnostics are tracked in the [active checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). The movie provider, catalog and identity failures have not yet been repaired.

## 1. Implementation decisions

| Area | Decision |
|---|---|
| Application structure | Keep the existing Core, WPF, and Tests.E2E projects, MVVM messaging, tab system, VLC, and WebView integration. Make targeted changes within these boundaries. |
| Navigation | Media group: Anime, Manga, Movies, Books, TV Shows, Cartoons. Utility group: Player, VA Detect, Watch Together, My List, Downloads, Settings. Use a flexible gap at wide widths; move the entire utility group to a second row when both groups cannot fit. |
| Styling | Keep shared resource dictionaries. Separate standard and compact dimensions from control templates; explicitly theme tab actions. |
| Movie/TV metadata | Use a qualified non-TMDB, keyless metadata system. Core discovery and playback, including sub/dub choices, must not require user-supplied API keys. Replace TMDB routing in IP05/IP06; a generic Archive video feed is insufficient. Existing TMDB implementation work is transitional, not the final provider decision. |
| Identity | Preserve provider, namespace, item ID, actual feature/series form, and exact unit. Browse category, translated title, and source URL must not determine persistent identity. |
| Source verification | Treat discovery, verified identity, usable transport, and actual playback as separate states. All source adapters use the same identity policy. |
| Data migration | Add contracts and compatibility adapters first. Version audiovisual JSON storage, preserve legacy aliases/rows, and migrate only unambiguous identities. |
| Resume authority | Keep SQLite as the authoritative per-work/per-unit position store. Audiovisual JSON records favorites/status and a last-opened-unit summary. Wire actual player progress into both through one ordered service. |
| Background work | Apply a shared scheduler and operation deadline to interactive scraper requests. Keep HTTP host throttling. Publish independent results incrementally. |
| Anime | Preserve existing behavior when no audiovisual context is supplied. Isolate the duplicate-resolution repair and regression-test any shared infrastructure change. |
| Tests | Use the existing xUnit/FlaUI project. Add focused fixtures and an STA render helper; do not create another broad test framework. |
| Release | Build and test an explicitly selected executable/package from a recoverable source snapshot. Passing compilation alone does not close a work package. |

### User preference - minimize required API keys

The user explicitly reaffirmed that fewer required keys are better, with the keyless anime sub/dub flow as the target experience. Do not introduce a mandatory key setup step for ordinary movie/TV/cartoon discovery or playback. The user further clarified that the target must use something other than TMDB. IP05 must qualify credential-free, non-TMDB metadata; IP06 must migrate production routing to it. This preference does not turn provider-wide language claims, selected preferences or generated ID URLs into proof of an individual source's identity/audio. TVmaze is now the Television default. Movies use typed Wikidata films with optional exact-ID IMDb artwork, independent of saved TMDB keys. Cartoon routing remains transitional. Preserve legacy IDs and saved data during migration; the reusable paging/outcome contracts remain useful.

### Additional implementation finding

The detailed code review for this plan found an incomplete audiovisual resume connection: `AudiovisualLibraryService.RecordProgressAsync` has no production callers. The catalog calls `RecordOpenedAsync`, while `PlaybackViewModel` falls back to display-title and source-URL-derived SQLite keys for non-anime playback. Its title stripping recognizes ` - Ep `, whereas TV labels use ` · SxxExx`. Consequently, changing a signed film URL can change its resume key, and TV identity depends on presentation text. **IP08 explicitly repairs the whole forwarding and persistence path**, rather than merely adding IDs to catalog cards.

## 2. Work packages and dependencies

The IDs in the R column refer to requirements in the recovery document. Size is relative implementation effort: S is a focused change, M spans several related files, L changes a cross-layer contract. These are not calendar estimates.

| Package | Deliverable | R | Size | Prerequisites |
|---|---|---|---|---|
| IP00 | Source baseline and isolated, exact-build testing | R01 | M | — |
| IP01 | Production composition seam, diagnostics, deterministic helpers | R01, R17 | M | IP00 |
| IP02 | Shared control templates and sizing | R03 | M | IP01 |
| IP03 | Responsive grouped navigation | R02 | M | IP02 |
| IP04 | Additive identity, paging, outcome, and verification contracts | R04–R07 | L | IP01 |
| IP05 | Non-TMDB keyless metadata qualification and decision record | R04 | M | IP04 |
| IP06 | Metadata routing, classification, search, paging, and catalog UI | R04–R05, R11 | L | IP04, IP05 |
| IP07 | Python/C# candidate evidence and consistent source verification | R06 | L | IP04 |
| IP08 | Library migration, playback context, and canonical resume | R05, R15 | L | IP04, IP07 |
| IP09 | Shared preparation state and bounded process startup | R09 | M | IP01 |
| IP10 | Scheduled, incremental audiovisual resolution | R07 | L | IP04, IP07, IP09 |
| IP11 | Single-pass automatic anime resolution | R08 | M | IP09 |
| IP12 | HLS body deadlines and session cancellation | R10 | M | IP01 |
| IP13 | Progressive books/Dub results and truthful failures | R11 | M | IP04, IP09 |
| IP14 | Poster request ownership and audiovisual virtualization | R12 | M | IP02, IP06 |
| IP15 | Ordered persistence away from the dispatcher | R13 | M | IP08 |
| IP16 | Settings, health, and action behavior alignment | R14 | M | IP03, IP06, IP09, IP10, IP13 |
| IP17 | Complete journeys, lifetime checks, and coverage reconciliation | R15–R17 | L | IP03, IP06–IP16 |
| IP18 | Verified release package and rollback record | R17 | M | IP17 |

Work can proceed in parallel after IP01: UI, preparation/streaming, and audiovisual contracts are separate work areas. IP05's provider decision gates the final default catalog, not UI or reliability repairs. Agree on IP04 contracts before concurrent edits to their consumers. Keep changes to `App.xaml.cs`, configuration defaults, and shared test infrastructure under one integration owner per batch.

## 3. Contracts to implement

Names in this section are **proposed** unless explicitly described as existing. Prefer adding related records to the current audiovisual model files; introduce a new file when it creates a useful boundary, not one file for every small record.

### 3.1 Identity and unit contracts

Existing `AudiovisualIdentity` already has `Kind`, `ContentForm`, titles, year, `TmdbId`, and `ImdbId`. Extend it with `PrimaryId`, `ExternalIds`, and explicit animation evidence. Retain legacy ID properties as compatibility projections during migration.

```csharp
// Proposed identity value; sample namespaces are part of the contract.
public sealed record AudiovisualExternalId(
    string Provider, string Namespace, string Value);

// Examples: tmdb/movie/123, tmdb/tv/123,
// imdb/title/tt1234567, internetarchive/item/identifier.
```

Implement one `AudiovisualIdentityKeys` helper for validation, normalization, candidate conflict detection, canonical keys, and legacy conversion. Provider/namespace normalization must not incorrectly change a provider's case-sensitive item value.

Rules:

1. `ContentForm` describes the actual feature or series. `Kind` remains a browse-category compatibility field; a cartoon may be either form. A selected tab never manufactures content form or a provider namespace.
2. Same-provider IDs in different namespaces are different works. Conflicting IDs within the same namespace prevent a verified match.
3. Freeze a persisted `WorkKey` when a library entry is created. Use a namespaced primary ID when known; use an explicitly local opaque key for unresolved legacy records. New, verified external IDs become aliases instead of silently changing the work key.
4. Changes to title, locale, year enrichment, poster, or playback URL do not change an identified work key. Category overlap between an animated film's Movie and Cartoon views must resolve to the same identified work when evidence supports it.
5. Freeze `UnitKey` on first persistence as well as `WorkKey`. For a feature use `feature`; for a known episode prefer validated `season:N:episode:N`, or use a verified provider episode ID when numbering is unavailable. Later provider IDs become verified unit aliases and cannot change the stored resume key. Missing episode fields never become episode 1 by default. Preserve season zero for supported specials; only accept it where provider metadata establishes that unit.
6. Keep title-based legacy records accessible, but do not merge ambiguous remakes or unrelated works by title alone.

### 3.2 Paging and provider outcomes

Extract the existing internal `IAudiovisualMetadataClient` from `TmdbMetadataClient.cs` into its own proposed interface file. Add a page API. Add a public paged catalog interface implemented by `MovieService`, `TvService`, and `CartoonService`; keep their existing list methods as explicit first-page adapters while consumers migrate.

```csharp
// Proposed signatures; records below specify their data.
Task<AudiovisualCatalogPage> GetPageAsync(
    AudiovisualCatalogRequest request, CancellationToken token);

IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(
    SourceSearchRequest request, CancellationToken token);
```

| Record | Required fields |
|---|---|
| `AudiovisualCatalogRequest` | Kind, Discover/Search mode, normalized query, page size, continuation token, locale. |
| `AudiovisualCatalogPage` | Items, next token, provider outcomes, `IsPartial`, `IsStale`. |
| `ProviderOutcome` | Provider ID, typed status, elapsed duration, optional retry time, redacted diagnostic code. |
| `ProviderFetchResult` | Typed outcome plus response text and cache metadata; replaces null-as-every-failure at the new API boundary. |
| `SourceSearchRequest` | Selected identity and unit, requested audio/subtitle preferences, explicit provider if any, operation ID. |
| `AudiovisualSourceUpdate` | Operation ID, stable candidate/source ID, update kind, evidence/source payload or provider outcome; terminal completion summary. |

Outcome statuses: `Success`, `Unavailable`, `Timeout`, `RateLimited`, `InvalidCredentials`, `NotConfigured`, `Unsupported`, `InvalidResponse`, `Disabled`, `Busy`. A successful empty response stays `Success` with zero items. Caller cancellation propagates as cancellation; an internal provider timeout becomes a provider outcome. The operation owner distinguishes its overall deadline from user cancellation.

Continuation tokens bind provider, mode, query, content form/category, locale, configuration revision, and upstream cursor. Treat them as opaque validated data. Never switch provider partway through a paging sequence; restart explicitly if fallback is needed. Keep prior visible rows when a later page fails. A bounded refill can examine additional upstream pages after filtering, but must retain a continuation token when more records remain.

### 3.3 Candidate evidence and matching

Extend `AudiovisualScraperSearchResult` and the corresponding Python output with optional IDs, titles, year, content form, animation evidence, unit, and evidence origin. Preserve the current title/provider/URL fields for transitional parsing. Missing new fields mean **Unverified**, not a successful exact match.

Extend `ExactAudiovisualMatcher` with a result carrying `Verified`, `Unverified`, or `Rejected` plus reason codes. Retain a boolean wrapper for existing callers while migrating. Verify independent candidate evidence before copying any selected-item presentation metadata onto a result. Apply the same policy to scraped, Archive, and manifest sources.

Separate `AudioEvidence` from subtitles and original-language metadata. Record provider item declarations or observed audio tracks with their origin; provider-wide language support and a user's selected preference do not prove an individual source's audio. Preserve the stricter Arabic-cartoon evidence path.

Updates may be CandidateDiscovered, VerificationChanged, SourceReady, ProviderFailed, or Completed. A SourceReady result must have verified identity and a supported transport; it does not yet prove playback started. Unverified candidates may be displayed as such, but cannot be auto-played or described as exact. Existing explicit browser-opening behavior must remain clearly distinct from verified in-app playback.

### 3.4 Playback context and progress

Add optional `AudiovisualPlaybackContext` to the end of existing playback message/method parameter lists. It carries frozen work key, exact unit key, identity/unit, title/poster metadata, and source identity/evidence. Transport headers continue through the existing copied header fields; do not persist cookies or signed URLs as identity.

Forward it through:

```text
AudiovisualCatalogViewModel.PlaySourceAsync
  → PlayMediaMessage
  → MainViewModel messenger handler → PlayMedia / PlayEmbed
  → PlaybackViewModel.LoadMedia / LoadEmbed
  → retry / native-to-browser fallback / source switch / unit navigation
  → ordered progress service → SQLite resume + audiovisual library summary
```

When context is absent, preserve existing anime and standalone-player key behavior. When present, do not parse the display title or hash the transport URL to derive resume identity. A separate audiovisual unit resolver must preserve season, language choice, context, and headers; the existing integer-oriented `EpisodePlaybackContext` is not sufficient for TV unchanged.

### 3.5 Ownership and service lifetimes

| Service/state | Lifetime and ownership |
|---|---|
| Configuration, provider coordinator, preparation state, scraper scheduler | One app-level instance, disposed during orderly application shutdown. |
| HTTP clients | Reuse existing long-lived clients; do not instantiate per card/candidate. |
| Catalog/source operation | One owner per action, linked to view lifetime; includes generation ID, deadline, and terminal state. |
| Shared candidate/image work | Reference-counted subscribers; cancel underlying work only when none remain or its service ends. |
| Playback context | Immutable per active session; capture it when enqueueing writes so later source/tab changes cannot redirect old progress. |
| Database work | Short-lived DbContext per serialized operation, or an exclusively owned context on one worker. Never share a DbContext concurrently across background tasks. |
| Views/view models | Preserve current tab ownership. Do not bind the lifetime of a running player's unit resolver to the originating details view. |

## 4. Initial operating policy

These are **proposed starting defaults for implementation and controlled testing**, not measured performance claims. Keep them in one validated options object mapped through `DomainHotSwapper`, and change them only with recorded evidence. Expose ordinary choices in Settings; keep advanced budgets in diagnostics/configuration unless users need them.

| Setting | Initial policy |
|---|---|
| `OtherMediaMetadataMode` | Default to the qualified non-TMDB keyless adapter selected in IP05. Migrate legacy `Auto`, `Tmdb` and `Keyless` values in IP06; do not select TMDB based on a saved key. |
| `OtherMediaMetadataFallback` | True in Auto. Try one alternate on provider failure within the same action budget; never replace a successful empty response. |
| `OtherMediaCatalogPageSize` | 30 items; clamp 10–60. |
| `OtherMediaMaximumRefillPages` | 3 upstream pages per requested page, retaining further continuation. |
| `InteractiveScraperConcurrency` | 2 app-wide Python scraper jobs; one browser maximum per job, with isolated owned profiles. Preparation has its own single-flight state. |
| `InteractiveScraperQueueLimit` | 16 pending jobs; deduplicate first, discard canceled jobs, and return Busy/queue status instead of growing indefinitely. |
| Interactive source deadline | 90 seconds total for a prepared environment; includes queue wait, HTTP, extraction, fallback, and cleanup initiation. Child budgets cannot exceed remaining time. |
| Preparation deadline | 180 seconds for the shared explicit preparation attempt; callers can stop waiting independently. Never hide this time inside a normal loading spinner. |
| Metadata/books provider deadline | 15 seconds including body processing; optional book scraper may use its existing 25-second child budget, without blocking other results. |
| Manifest/key deadline | 15 seconds end to end, linked to playback session. |
| Streaming-body inactivity | 20 seconds without useful bytes; reset on progress, with no short fixed cap on a healthy long stream. |
| Images | 6 concurrent fetches, 2 concurrent decodes, 15-second body-inclusive operation deadline; retain existing size and cache limits. |

User cancellation stops the caller immediately and starts bounded cleanup of owned work. A caller abandoning a shared preparation task must not cancel other waiting consumers; explicit Cancel Preparation or shutdown owns cancellation of that shared task. The same distinction applies to shared images/candidate lookups.

**Metadata routing:** The qualified non-TMDB keyless adapter handles normal discovery regardless of whether a legacy TMDB key is stored. Migrate old mode settings with a clear migration notice; preserve saved library identities and settings backups. Do not silently fall back to TMDB. Report the selected provider's unavailable, rate-limited or unsupported state accurately. Cache entries are separated by provider, locale, query, page, and credential/configuration revision; raw credentials must not appear in cache keys or logs. `OtherMediaEnableInternetArchive` becomes a playback-source setting and no longer controls the general metadata feed.

## 5. Work-package instructions

The following sections specify implementation steps, touched areas, tests, and completion evidence. Existing file links point to current source; proposed files are named as future additions.

### IP00 — Preserve source and establish the test baseline

1. Inventory tracked and untracked source, assets, lockfiles, project files, Python scripts, and test infrastructure. Save a recoverable source snapshot and a manifest with HEAD, dirty-source identity, hashes, SDK, and Python version. Review generated/private files before including them; do not use an indiscriminate `git add .` or reset to published Git.
2. Record the executable paths currently in use and the audit's 242-passed/22-skipped result as historical baseline evidence. Use a dedicated implementation branch such as `codex/app-recovery` when code work begins.
3. Record baseline screenshots at 900/1280 DIP and representative timing traces. Do not start new tests that can touch real OtherMedia storage until IP01's full data-root isolation is in place.
4. Store machine-specific results under `.artifacts/implementation/`; commit deterministic fixtures and source changes separately from these generated artifacts.

**Deliverable:** baseline source manifest, restoration instructions, test/build identity, and a list of intentionally excluded files. Verify the snapshot can reconstruct source, including the untracked movie subsystem. Preserve any user build/profile that will be needed for comparison.

### IP01 — Make production wiring and tests reproducible

**Existing areas:** [App.xaml.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/App.xaml.cs), [AppFixture.cs](C:/Users/user/animeapp/UniversalMediaOS.Tests.E2E/Infrastructure/AppFixture.cs), [run-e2e-tests.ps1](C:/Users/user/animeapp/run-e2e-tests.ps1), [AppLogger.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Helpers/AppLogger.cs).

1. Extract the current private `App.ConfigureServices` registration into an internal composition helper callable by app startup and tests. Allow explicit paths/options and controlled HTTP/process substitutes in tests while exercising the same Movie/Tv/Cartoon registrations as production. Retain existing lifetimes.
2. Add proposed `AppDataPaths` with an explicit `UNIVERSAL_MEDIA_OS_DATA_ROOT` override. Route configuration, SQLite, audiovisual JSON, books/progress, Python/browser profiles, caches, and logs through it or existing constructor path parameters. The current fixture redirects APPDATA, while several services use `SpecialFolder.LocalApplicationData`; changing APPDATA alone does not isolate them.
3. Update `AppFixture` to set the override in the launched process and inject test paths into in-process services. Confirm every writable app path stays under the fixture root. Restore process environment changes at teardown. Preserve the existing Secondary-monitor test preference.
4. Require `UNIVERSAL_MEDIA_OS_EXE` for implementation/release verification. Extend the runner with an explicit executable option and make it validate that override instead of first requiring a default-bin executable. Disable Debug/Release fallback in verification mode.
5. Add structured operation events: operation ID, feature, stage, elapsed time, result count, active/queued work count, cancellation/deadline reason, and safe provider ID. Add a small diagnostics/build-info view. Preserve redaction; do not log signed URLs, cookies, tokens, or raw private queries.
6. Extend the existing `MockHttpServer` with delayed headers/bodies, controlled status codes, paginated responses, and observable request counts. Add a small dedicated STA dispatcher helper for WPF render tests and a fake process runner for preparation/resolver tests.

**Tests:** complete path isolation; exact executable selection; DI parity; logging redaction; controlled server teardown. Keep test parallelization policy unchanged until isolation is demonstrated.

**Done:** deterministic tests exercise real production composition, target one identified build, and cannot write outside their owned profile. This is the prerequisite for expanding desktop coverage.

### IP02 — Correct reusable control styles

**Existing areas:** [Controls.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Controls.xaml), [Tokens.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Themes/Tokens.xaml), [ThemeRuntime.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/Helpers/ThemeRuntime.cs), Playback/Settings/Audiovisual/Search/AnimeDetails views.

1. Keep `AppComboBox` as the standard and implicit style, remove global `MinWidth=220`, and move dimension choices to consumers/style variants.
2. Add `CompactComboBox` based on the same corrected template. Add compact height/padding metrics to tokens and `ThemeRuntime.ApplyUiMetrics`/`ApplyDensity`; ensure minimums cannot override intended compact dimensions.
3. Replace the inner native ToggleButton template with a full-size border and arrow. Put the selection presenter in the outer grid. Preserve `PART_Popup`, `IsDropDownOpen`, `SelectionBoxItem`, selection templates/string formatting, `DisplayMemberPath`, max popup height, keyboard behavior, and flow direction.
4. Apply compact style to playback speed/quality selectors at their intended 72/88-DIP widths. Preserve `PlaybackRates`, `PlaybackRate`, `QualityOptions`, `SelectedQuality`, and `DisplayMemberPath="Label"`. Fit the audiovisual language selector inside its 170-DIP parent. Keep normal Settings sizing and `SelectedValuePath="Tag"`.
5. Add `TabIconButton` with explicit normal/hover/pressed/focus/disabled states. Replace hardcoded mismatched hover foreground/background pairs with theme resources. Keep current commands and `CanExecute` behavior.

**Tests:** proposed `ComboBoxRenderTests` and `TabChromeRenderTests` load compiled resources on STA, measure actual chrome/selection bounds, and render both themes. Cover compact/standard controls, label templates, long text, popup selection, and first/last/single-tab disabled arrows. Retain relevant `SettingsTests`, `PlaybackTests`, and `NonSettingsInteractionRegressionTests`.

**Done:** the original geometry defects fail before the repair and pass afterward; native white disabled rectangles are absent from rendered evidence.

### IP03 — Implement grouped responsive navigation

**Existing areas:** [MainWindow.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml), [MainWindow.xaml.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/MainWindow.xaml.cs). **Proposed addition:** `UniversalMediaOS.WPF/Controls/GroupedNavigationPanel.cs`.

1. Replace the single navigation `WrapPanel` with the two groups specified in section 1. Keep one element/command tree; do not create separate duplicate wide and narrow buttons.
2. In `MeasureOverride`, measure both groups' unconstrained desired widths and compare their sum plus a 24-DIP minimum gap with available width. In `ArrangeOverride`, place them at logical leading/trailing edges if they fit, otherwise place the whole utility group on row two, trailing aligned. Respect RTL without applying a second manual mirror. If a group itself cannot fit, give that group an intentional bounded arrangement rather than isolating Settings.
3. Keep the brand/window-control title bar, tab row, content, and status bar separate. Preserve `AppFlowDirection`, content-only `ActiveContentScale`, tab view-model lifetimes, and caption hit testing.
4. Apply `TabIconButton` to move/close actions. Preserve `SelectTabCommand`, `CloseTabCommand`, `MoveTabLeftCommand`, `MoveTabRightCommand`, drag handlers, and tab-scroll handlers. Add stable automation IDs independent of translated labels.
5. Do not alter `EnterAppFullscreen`, `ExitAppFullscreen`, `ClampWindowToNearestWorkArea`, or `UpdateWindowStateVisuals` in this package unless its rendered test demonstrates a specific interaction defect.

**Tests:** proposed `NavigationLayoutRenderTests` at 900/1280 DIP and immediately around the measured fit threshold; English/Arabic, supported densities, light/dark, and 90/100/140% content scales. Assert nonintersecting group bounds, stable Settings membership, reachable actions, and unchanged `Tabs.Count`/`ActiveTab` during resize. Use FlaUI for command/keyboard checks and manually verify real DPI/monitor transitions.

**Done:** screenshots show the recovered grouping principle at wide and compact widths, while current window and tab behavior remains functional.

### IP04 — Introduce the contracts without breaking consumers

**Existing areas:** [AudiovisualModels.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualModels.cs), [TmdbMetadataClient.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/TmdbMetadataClient.cs), [AudiovisualCatalogServices.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualCatalogServices.cs), [ProviderRequestCoordinator.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/ProviderRequestCoordinator.cs).

1. Implement section 3's identity helpers, typed outcomes, page models, evidence models, and optional playback context with safe defaults for existing JSON.
2. Add a typed fetch method to `ProviderRequestCoordinator`; keep `GetStringAsync` as a temporary wrapper. Distinguish disabled, invalid credentials, backoff/rate limit, timeout, invalid response, and successful empty data. Preserve retry timing, cache bounds, host restrictions, and throttling.
3. Add `GetPageAsync` to metadata clients and a paged public interface. Keep legacy `SearchAsync`/`GetPopularAsync` collecting the first page until their callers migrate. Do not add default interface implementations that return fabricated empty pages.
4. Add `FindSourceUpdatesAsync` as the new source contract; the old `FindSourcesAsync` may collect updates during migration. Explicitly mark these adapters for removal when no production caller needs them.
5. Add provider capability flags for discovery, search, continuation, locales, and episode metadata. Define `GetUnitsAsync(identity, season, token)` with typed outcomes for supported series browsing; unsupported is distinct from an empty season.

**Tests:** serialization of old/new payloads; provider-ID normalization and namespace collisions; conflicting IDs; alias stability; typed fetch outcomes; successful empty result semantics; caller cancellation. Compile all three projects after each contract step.

**Done:** contracts are implemented with representative fixtures; existing behavior compiles via explicit adapters, and dependent packages can work against the same definitions.

### IP05 — Qualify the non-TMDB keyless metadata provider

**Deliverable:** [provider decision record](C:/Users/user/animeapp/docs/decisions/keyless-metadata.md), adapter prototypes and deterministic response fixtures. TVmaze is selected and enabled for Television as of September 13; the film and combined Cartoon strategy remains open.

1. Compare a small set of at most three explicitly named non-TMDB, keyless candidates, starting with feasibility of the existing IMDb client and suitable structured alternatives found through primary provider documentation. Evaluate Archive only for an honestly scoped collection capability; do not accept its generic video feed as general film discovery.
2. Use one fixed sample spanning ordinary films, remakes/sequels, TV series, animated features, and animated series. Record expected identities, search variants, missing information, and pagination results. Separate provider coverage from playback availability.
3. Verify a clean profile without a user key or browser login, stable IDs, real movie/series classification, partial search, additional pages, failure/cancellation behavior, and a workable episode-metadata path. Verify that opening a catalog does not require browser crawling per card.
4. Record runtime/setup needs, response formats, caching/throttling constraints, and any requirements relevant to shipping the chosen adapter. Capture minimum necessary response fixtures without session credentials.
5. Choose the passing candidate, document why, and implement its configuration/capabilities through the IP04 interface. If no candidate meets useful keyless film/TV discovery, record that acceptance criterion as unresolved and the specific product decision needed. Continue independent packages; do not substitute placeholders or silently require a key while declaring R04 complete.

**Done:** an evidence-backed provider decision and prototype pass the fixed sample through production-style routing. This is the only provider-selection gate, not a reason to redesign the rest of the app.

### IP06 — Implement metadata routing, pagination, and catalog interaction

**Existing areas:** metadata router/clients/services, [AudiovisualCatalogViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/AudiovisualCatalogViewModel.cs), [AudiovisualCatalogView.xaml](C:/Users/user/animeapp/UniversalMediaOS.WPF/Views/AudiovisualCatalogView.xaml), configuration and DI.

1. Implement the routing table in section 4 with the qualified non-TMDB adapter and migrate legacy TMDB selection settings. Preserve legacy external IDs for matching and saved-library compatibility; remove the TMDB key requirement from normal discovery setup. Treat the Archive enable flag as a source option; preserve its prior value during migration. Add new metadata settings with defaults only when absent.
2. Parse actual content form and provider namespace. Apply animation/category filters using evidence; never stamp a Movie/TV request kind onto every generic record. Use type-scoped animation discovery where the provider supports it instead of filtering one generic trending page indefinitely.
3. Replace full-title equality with ranked discovery matches. Preserve alternate titles/year distinctions. Add provider paging and bounded refill; deduplicate using verified external identity and actual namespace, not bare numeric IDs.
4. In the VM add `HasMore`, `IsLoadingMore`, `LoadMoreCommand`, page token, and provider outcomes. Separate catalog, source, and download cancellation/generation state. Snapshot query/filter/locale at request start and check the generation before any collection update.
5. Append a completed page in one UI update, retain current rows on later-page failure, and reset continuation only when the query/filter/provider sequence changes. Keep a clear successful-empty state distinct from failed/offline/configuration states.
6. Present actual seasons/episodes from `GetUnitsAsync` where supported. Retain a clearly labeled manual unit path only where needed; verification must still establish the selected unit before claiming an exact source. Feature items must not show episode controls.

**Tests:** extend `OtherMediaAudiovisualServiceTests` with mixed nonempty data, two-page filtered responses, partial titles, same numeric movie/TV IDs, blank/invalid keys, Archive-disabled routing, and stale page results. Add desktop Load More and season-selection tests through production DI.

**Done:** browse/search/page behavior works from a fresh profile, and the old identical Movie/TV 50-title feed is no longer reproduced through incorrect type stamping.

### IP07 — Preserve evidence across Python and C#

**Existing areas:** [audiovisual_scraper.py](C:/Users/user/animeapp/UniversalMediaOS.Core/audiovisual_scraper.py), [AudiovisualScraperEngine.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualScraperEngine.cs), scraper/Archive/manifest providers, `ExactAudiovisualMatcher`.

1. Add optional evidence fields to search/resolve JSON and C# records. Keep stdout machine-readable and progress on stderr. Accept older payloads as unverified during rollout; detect an incompatible protocol explicitly instead of returning an unexplained empty list.
2. Preserve observed IDs/title/year/form/unit before ranking. Token similarity may rank candidates but cannot prove identity. Reject conflicting sequels/years/units; a trustworthy exact shared ID can establish a match with missing year data.
3. Stop stamping the requested identity/unit onto a scraped candidate as matching evidence. An ID merely inserted into a generated embed URL, copied from arguments, or echoed into a response object is request-derived, not independently observed evidence. Track that provenance and require independent provider/item evidence before verifying. Use the new matcher result for all providers, including direct Archive lookup and manifest results. Resolve a preserved Archive item ID directly rather than repeating title search.
4. Distinguish per-item audio declarations and observed tracks from provider-wide languages, requested language, subtitles, and original language. Review manifest language inheritance as well as the scraper adapter. Unknown audio remains unknown and cannot satisfy a strict verified-language filter.
5. In source presentation, expose verified, unresolved, and rejected/provider-failure outcomes honestly. Auto-play and the “exact source” count use only verified sources. Identity verification and network/source-safety checks remain separate operations.

**Tests:** convert the existing AST wrong-sequel probe into maintained fixtures and retain positive matches. Add missing-year exact ID, remakes, wrong season/episode, cartoon feature/series, conflicting IDs, an echoed requested ID that remains unverified, old JSON payloads, and unknown audio. Verify headers/cookies/referer survive adapter processing without becoming persisted identity.

**Done:** both known wrong-sequel cases are rejected, valid candidates remain usable, and every source adapter provides evidence under the same policy.

### IP08 — Migrate library identity and wire real playback progress

**Existing areas:** [AudiovisualLibraryService.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Audiovisual/AudiovisualLibraryService.cs), `AudiovisualCatalogViewModel`, [MyListViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MyListViewModel.cs), [PlayMediaMessage.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlayMediaMessage.cs), [MainViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/MainViewModel.cs), [PlaybackViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/PlaybackViewModel.cs), database service boundary.

1. Add `AudiovisualLibraryKey.Create(AudiovisualIdentity)` and one entry-to-identity reconstruction helper. Migrate both catalog library loading and My List; remove their independent `tmdb:`-only parsing.
2. Read the existing JSON list and a new versioned envelope containing entries, work keys, and verified aliases. Back up the original before the first successful atomic replacement. Preserve favorites/status/last-opened data. Infer old TMDB namespace only when form/category makes it unambiguous; retain uncertain entries without automatic title merging.
3. Append optional audiovisual context through every forwarding site listed in section 3.4. Include retry, stream-to-browser fallback, reconnect, source changes, and unit navigation. Capture immutable old context before replacing the active session.
4. Use canonical work/unit keys for SQLite resume whenever context exists. Leave the anime path unchanged without context. Preserve old rows; opportunistically import a legacy position only when the current identity/unit maps unambiguously. Do not promise recovery of an expired source-hash key whose original URL is unavailable.
5. Introduce an ordered progress service: load position; throttle ordinary writes; flush on pause/source switch/close/end; update `AudiovisualLibraryService.RecordProgressAsync` as a last-unit summary. SQLite remains per-unit authority. Capture context and progress in queued writes, prevent stale writes after completion, and define completion/resume-clearing consistently with existing thresholds.
6. Add an audiovisual unit resolver with a lifetime independent of the details VM. It returns verified source, unit, context, and complete transport headers for previous/next and season transitions. Do not force TV into the existing anime integer-only callback.

**Tests:** proposed `AudiovisualIdentityMigrationTests` and `AudiovisualPlaybackContextTests`; old-list migration/idempotence/failure recovery; Movie/Cartoon convergence with conflicting legacy fields; work/unit aliases and year/title/episode-ID enrichment after progress exists; My List round trip for IMDb/Archive; same film across changed URLs; remakes and S1E1/S2E1 separated; all forwarding/retry paths; closing the originating details tab; late progress after source switch/completion. Retain `PlaybackResumeRegressionTests`, `FavoriteMediaRegressionTests`, and anime tracking tests.

**Done:** real player progress reaches the audiovisual summary; a film resumes after its source URL changes; per-season episodes stay separate; old user data remains accessible. Record migration limitations rather than silently discarding ambiguous history.

### IP09 — Implement preparation and process ownership

**Existing areas:** [PythonBootstrapper.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/PythonBootstrapper.cs), all three scraper engines, app startup/status, settings health.

1. Model preparation as NotStarted/Preparing/Ready/Failed/Canceled with missing-import details and a shared task. Give the preparation operation its own lifetime and budget; each caller can independently cancel waiting. Explicit preparation cancel and app shutdown own the underlying task.
2. Resolve Python, deploy scripts into the owned data root, run one import probe, install only missing packages, drain both streams concurrently, and recheck imports. Set Ready only after all required imports and expected script/protocol checks succeed.
3. Return structured process failure/timeout/cancellation information to callers. Include queue/start/drain/cleanup in the declared operation policy; do not restart a fresh timeout for every nested stage.
4. Share the same health snapshot with Settings and the status bar. Add Retry/Repair behavior that can recover in the same session. Cache successful readiness by runtime/script/dependency revision; invalidate when that revision changes.
5. Use testable process-launch seams and track only owned process trees/profiles. On shutdown/cancel, terminate owned work and await bounded output draining. Never use broad process-name termination.

**Tests:** fake missing imports, failed pip exit, output-pipe saturation, timeout, cancellation, concurrent callers, a canceled waiter with another active waiter, success caching, and retry recovery. Retain `OfflineBootstrapRegressionTests` and existing bootstrap tests.

**Done:** false Ready states disappear, preparation has visible bounded behavior, and ordinary prepared requests no longer repeat installation.

### IP10 — Schedule incremental audiovisual source resolution

**Existing areas:** `AudiovisualSourceResolver`, `ScraperAudiovisualSourceProvider`, all scraper process launch sites, `AudiovisualCatalogViewModel`. **Proposed addition:** shared `ScraperWorkScheduler` and a small operation-budget helper.

1. Enforce section 4's shared scraper concurrency/queue policy across interactive movie, anime, and book process launches. Await readiness before acquiring an execution slot. Do not hold a slot while starting nested work that needs the same slot; keep alternate-provider traversal inside its one scheduled job.
2. Rank/deduplicate candidates before queueing. A job identity includes work/unit, provider/candidate, language preferences, and relevant configuration revision. A single operation shares an attempted-candidate set so different candidates cannot redundantly repeat the same alternate-provider crawl.
3. Implement `FindSourceUpdatesAsync` using a bounded async update stream. Yield successful independent providers as they finish; prevent a slow Archive loop or scraper from withholding an already ready source. Each child receives remaining budget and cancellation.
4. Make `OpenDetailsAsync`/`OpenItemAsync` display metadata immediately. Reuse a valid cached verified source if present; otherwise show an explicit Find Sources/Play action to start expensive work. Keep source refresh and add a dedicated `CancelSourceLookupCommand`; it cancels the current lookup while preserving metadata and already usable sources, then sets an explicit terminal state. Selecting a ready source cancels or deprioritizes unneeded alternatives.
5. Consume updates on the dispatcher with operation/generation checks. Preserve selected source and stable ordering when later results arrive. A provider failure updates its status without clearing successes. Cache verified metadata and short-lived source evidence separately from expiring transport URLs.

**Tests:** fake fast/slow/failed source providers; global cap across tabs; queue backpressure; cancellation before slot acquisition and during extraction; subscriber-aware shared work; no late collection updates; one Completed event; headers preserved; deadline includes queue time. Verify that process slots return to baseline after every terminal state.

**Done:** the first usable source appears independently of the slowest provider, and instrumentation proves bounded process/browser work.

### IP11 — Remove duplicate automatic anime resolution

**Existing areas:** [TripleNetHandoff.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Routing/TripleNetHandoff.cs), [ScraperEngine.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Services/ScraperEngine.cs).

1. Introduce an internal resolution-attempt result distinguishing direct media, browser fallback, exhausted search, deadline, and failure. Preserve enough fallback information from one resolver invocation.
2. Refactor `ResolveBestSourceAsync` to consume that result once. Remove the second identical `_scraper.ResolveAsync` in the automatic Tier 2 branch. A manual WebView request may initiate its own one discovery attempt; an exhausted automatic request may not restart it.
3. **Corrected September 13 after the user's reminder:** normal anime Watch and episode navigation use automatic EverythingMoe/The Index discovery for both Sub and Dub. Remove the saved-provider dropdown and fixed domain seeds; retain the last discovered index list during an index outage. Do not require users to maintain URLs. Preserve source/header registration and effective site/deadline limits. The Player's explicit Open URL action remains available for user-supplied links; it is separate from automatic provider discovery.
4. Ensure cancellation/deadline never starts another fallback. Do not reintroduce the older resolve→search→many-extract cascade that current code removed.

**Tests:** `AutomaticResolutionRegressionTests` with invocation counts and direct/browser-only/exhausted cases; cancellation at each boundary; Sub/Dub and next-episode regression. Test index refresh/domain replacement, saved-list outage fallback and removal of legacy seeds. Use fake processes/providers for deterministic cases and separately recorded live discovery/playback smoke.

**Done:** an exhausted automatic request starts one complete indexed resolver, and supported success/fallback paths remain functional.

### IP12 — Give HLS requests a session lifetime

**Existing area:** [HlsLoopbackProxy.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Streaming/HlsLoopbackProxy.cs), playback registration/disposal sites.

1. Create cancellation ownership per proxy session and link it to every manifest/key/segment request and output copy. Track active tasks in a bounded request set; attach final cleanup/observation of faults.
2. Apply body-inclusive manifest/key deadlines. Use progress-reset inactivity limits for segment/media copying; do not apply a short total timeout to a healthy full-length stream.
3. Cancel the old session on source switch or playback close. Stop accepting its requests, abort/close owned reads and response streams, and await draining up to the cleanup policy during shutdown. Preserve active requests for other sessions.
4. Retain redirect credential handling, source validation, HLS rewriting, and session ownership checks. Distinguish timeout, session cancellation, and upstream failure in diagnostics.

**Tests:** local server stalls before/after headers and halfway through bodies; healthy slow continuous stream; session close/switch; two simultaneous sessions; concurrency cap; redirects and header replay. Extend `PlaybackHardeningRegressionTests` where appropriate and add a focused body-timeout fixture.

**Done:** stalled transfers end under the declared policy, healthy playback continues, and closed sessions release their work.

### IP13 — Publish books and Dub results progressively

**Existing areas:** [BookCatalogService.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/OtherMedia/Books/BookCatalogService.cs), public book providers, [BookBrowseViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/BookBrowseViewModel.cs), [SearchViewModel.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/ViewModels/SearchViewModel.cs).

1. Add a book-search update API alongside the current collector method. Emit each provider page/outcome when it finishes, and reuse existing duplicate-merge/ranking logic while preserving stable visible items and useful partial data.
2. Catch provider-owned timeout separately from caller cancellation, including body parsing. Update the VM's terminal/loading state in every path. A slow optional Python provider never gates already returned public-catalog results.
3. In Dub mode, populate cached confirmed matches immediately and append newly confirmed matches as checks finish. Keep unknown candidates explicitly pending or in a separately labeled state; do not silently count them as dubbed. Any/default mode must not wait for these checks.
4. Preserve per-host throttling, backoff, visible-row enrichment limits, and query generation cancellation. Do not obtain speed by increasing uncontrolled external concurrency.

**Tests:** one immediate book provider plus a timed-out provider; duplicate updates; user cancellation versus HTTP timeout; old query finishing late; fast cached Dub answers with delayed unknowns; Any mode unaffected. Extend `BookSubsystemTests`, `DubAvailabilityUiRegressionTests`, and `DubAvailabilityServiceTests`.

**Done:** partial results appear early, remain usable after provider failure, and every operation reaches a truthful terminal state.

### IP14 — Bound image work and virtualize expanded grids

**Existing areas:** [AsyncImageLoader.cs](C:/Users/user/animeapp/UniversalMediaOS.WPF/Controls/AsyncImageLoader.cs), audiovisual grid, `SearchVirtualizationRegressionTests` patterns.

1. Keep the attached-property API. Move fetching into a shared service with bounded work and in-flight URL coalescing; include decode width in decoded-image cache identity so a small thumbnail does not satisfy a larger request incorrectly.
2. Subscribe to image load/unload and URL changes. Detach each consumer on unload; cancel shared fetch only when no consumer remains. Restart a still-bound uncached image when loaded again. Reject stale completions by request identity before assigning `Image.Source`.
3. Link deadlines through body read and decode cancellation. Preserve compressed-size caps, downsampling, freezing, existing cache bounds, and background decode. Handle failures with a stable placeholder visual, not a permanently busy state.
4. Replace the audiovisual `ItemsControl`/`WrapPanel` with recycling rows or another measured virtualized approach compatible with paging. Reuse the existing anime row-virtualization approach where suitable; do not rewrite that working implementation. Keep selection, scroll position, and stable card identity through reflow.

**Tests:** extend `AsyncImageLoaderRegressionTests` for duplicate fetches, unload/reload, two consumers, stalled body, differing decode widths, and late URL completions. Add a 1,000-item deterministic audiovisual grid fixture and assert bounded realized containers around visible rows rather than just matching XAML strings.

**Done:** repeated scrolling/tab cycles reach stable resource usage and do not retain detached poster work.

### IP15 — Move ordered persistence off the UI dispatcher

**Existing areas:** [DatabaseContext.cs](C:/Users/user/animeapp/UniversalMediaOS.Core/Data/DatabaseContext.cs), `PlaybackViewModel`, voice-cast/VA callers; IP08 progress service.

1. Measure lock contention and dispatcher heartbeat around `ConfigureResumeState`, `LoadResumePosition`, and save/close paths. Audit every DbContext access before choosing ownership.
2. Route blocking SQLite operations through a bounded serialized worker using a context per operation, including creation/migration on that worker. SQLite async method names alone do not establish nonblocking native I/O; keep the dispatcher independent of the worker.
3. Capture immutable keys/context on enqueue, coalesce ordinary progress writes per session, and preserve ordering for seek, pause, close, and completed-state writes. Load asynchronously and apply a resume seek only if the same playback generation is still active.
4. Add `FlushAsync`/orderly close behavior to the progress boundary. Keep queues bounded, observe failures, and prevent teardown from accessing disposed native player handles. Do not make final persistence wait on the dispatcher while the dispatcher waits on worker shutdown.

**Tests:** three-second write contention while navigating; delayed resume load after a source switch; out-of-order progress attempts; final completion cannot be overwritten; app/tab flush; legacy migration and failure recovery. Retain cleanup, resume, and Core hardening regressions.

**Done:** measured UI responsiveness survives controlled database delay, and progress remains correct and durable under rapid navigation.

### IP16 — Align settings, health, and actions

**Existing areas:** `DomainHotSwapper`, `SettingsViewModel`, `SettingsView.xaml`, localization runtime, shell/status UI, feature command states.

1. Group metadata selection, playback-source settings, and preparation health within the existing merged Providers & Scrapers section. Rename misleading labels/tooltips to describe the new routing behavior. Preserve secret/password binding and existing settings migrations.
2. Map new options through validated defaults/ranges. Preserve user overrides; never rewrite the whole configuration with defaults. Bump metadata configuration revision and cancel/reset affected paging/source operations when relevant settings change.
3. Bind Ready/Preparing/Failed/Retry to IP09's shared state. Show invalid metadata credentials and fallback use separately from source availability. Keep the About/diagnostics build identifier and executable path from IP01.
4. Produce a button/action matrix for navigation, save/refresh/cancel, language/provider selection, source opening/play/download, tab movement, standalone file/URL load, and player actions. For each, verify the command's actual result and disabled prerequisite; use stable automation IDs.

**Tests:** saving/relaunching new and old settings; invalid values clamped/reported; Archive source toggle does not erase discovery; health matches import results; language change/RTL; keyboard actions; actual command outcomes. Preserve current Settings, Navigation, and NonSettings regression coverage.

**Done:** the visible configuration accurately predicts behavior and important actions have tested outcomes.

### IP17 — Validate complete journeys and resource lifetimes

1. Execute section 7's matrix against the exact built executable. Separate deterministic tests from live provider verification and record failures as well as successes.
2. Run 20 repeated open/search/play/cancel/close cycles. Check owned requests/processes, shared scheduler slots, subscriptions, and memory settling. Closing one tab must not stop another or cancel shared work with remaining subscribers.
3. Verify download queue restart/pause/resume/cancel, My List reopening, book reading/progress, manga chapter navigation, and two controlled Watch Together clients with reconnect. These are preservation/acceptance tasks, not assertions that every feature is currently defective.
4. Inventory the 22 existing skipped tests. For each release-relevant scenario, add a real deterministic or desktop replacement; otherwise record exactly what remains unvalidated and why. Do not count skips as passed coverage.
5. Reconcile the R01–R17 checklist with code, screenshots, traces, and test evidence. Fix failures in their owning package; avoid a broad unrelated cleanup at this stage.

**Done:** all required user journeys have evidence, performance claims are backed by measurements, and outstanding provider limitations are explicit.

### IP18 — Produce and verify the release artifact

1. Build/publish from the recorded final source snapshot using the repository's pinned SDK/locks and a clean, isolated output directory. Keep the existing runtime strategy unless a dependency-distribution change has been separately implemented and validated.
2. Verify inclusion of all three Python scripts, required assets, native VLC components, and required Windows/WebView runtime behavior. Record dependencies that remain installer/runtime prerequisites.
3. Include a source/build manifest and concise release notes listing repaired behavior, migration behavior, validation evidence, and remaining limitations. Include backup/recovery instructions for migrated user data.
4. Launch the actual packaged executable through the explicit path override with a fresh isolated profile, then with a copied legacy test profile. Repeat startup/settings, representative movie/TV playback, and resume migration smoke checks. Do not validate only the Debug build and ship an unrelated Release output.
5. Package/release delivery is distinct from publishing remotely. This plan requires a verified local artifact; any later upload or deployment follows the user's requested scope.

**Done:** the deliverable package identifies its source, passes the final smoke matrix, and has a tested recovery record.

## 6. Migration and compatibility rules

### Configuration

Add a configuration schema revision and perform additive, idempotent migration. Preserve existing values, including credentials and the Archive source switch. Add new metadata/work-budget settings only where absent. Validate ranges on read/save. Document that separating Archive source configuration from metadata fixes an old coupling. Clear or revise affected cache/continuation entries when metadata provider, locale, or credential revision changes.

### Audiovisual library

Read the old JSON array and proposed version-2 envelope. Before first conversion, preserve a known-readable backup. Build a candidate migrated snapshot, validate it, write a temporary file in the destination directory, and replace atomically; publish the new in-memory dictionary only after persistence succeeds. Retain the existing source on failure and make retry safe. Store legacy IDs/aliases, preserve ambiguous records, and never merge two conflicting provider IDs by title.

A version-2 work entry contains frozen work key, external IDs/verified aliases, actual content form, categories supported by evidence, title/poster, favorites/status, last-opened unit, and progress summary. Keep provider episode identity or explicit season/episode in unit data. Shared entry reconstruction must serve both catalog and My List.

**Collision policy:** Two old Movie/Cartoon entries may refer to the same animated feature because legacy keys include category. Reconcile only after shared provider identity and form establish equivalence. Preserve both original records and legacy IDs in migration provenance, union verified aliases/categories, retain favorite if either entry is favorited, and take status/display enrichment from the most recently updated entry with ordinal legacy-ID ordering as a tie-breaker. Use the latest last-opened timestamp for the summary. If multiple frozen work keys already exist, choose a survivor deterministically by ordinal key order and retain the other keys as aliases; never overwrite one dictionary entry silently. Reconcile resume per verified unit, using the latest timestamp where available rather than the furthest position. Missing/conflicting timestamps or ambiguous units retain their original rows for later reconciliation and are not guessed into one position. Test both equivalent category entries and records that must remain separate.

### Resume

Continue using the existing SQLite resume table where its string keys suffice. New audiovisual work/unit keys are explicit and independent of the URL; anime remains on its existing keys when no audiovisual context exists. Preserve all legacy rows. Import a legacy position only with an unambiguous mapping and record the alias used. When old signed URLs or ambiguous title keys prevent matching, preserve the row and report the limitation rather than guessing another work's progress.

SQLite is authoritative for per-episode positions; audiovisual JSON is the library/last-unit summary. A failed summary write must not undo a successful authoritative resume write. Retry or repair the summary on the next read/update. Serialize final writes and prevent older queued progress from resurrecting a completed position.

### Python and public service APIs

Deploy the Python script and matching C# contract in the same build. Additive JSON fields are optional while compatibility is needed. Older evidence-free payloads parse as unverified. A protocol mismatch is an explicit invalid-response/preparation condition, not an empty success. Preserve legacy C# collector methods until all production consumers and relevant tests use the new page/update APIs; then remove unused adapters in a separate reviewed cleanup.

### Rollback

Before data-writing packages IP08/IP15 and the release smoke, preserve a coherent test-profile snapshot. Prefer reverting a code package through version control. If the old application cannot read migrated data, use the pre-migration profile copy for rollback testing; restoring it may omit progress created after that snapshot, which must be stated. Never run old and new writers against the same profile concurrently, and never reset the whole working directory to the June Git baseline as a recovery shortcut.

## 7. Verification specification

### 7.1 Deterministic suites

Proposed test files below are implementation deliverables, not files already created. Extend existing suites instead when that keeps closely related cases together.

| Suite / fixture | Required assertions | Package |
|---|---|---|
| `ApplicationIsolationTests` | All app writes under test data root; exact EXE chosen; production DI routes exercised. | IP01 |
| `ComboBoxRenderTests`, `TabChromeRenderTests` | Actual compact bounds, full inner chrome, popup/selection behavior, themed disabled states. | IP02 |
| `NavigationLayoutRenderTests` | Measured wide/compact transition, utility group intact, nonoverlap, RTL, tab lifetime unchanged. | IP03 |
| Extended `OtherMediaAudiovisualServiceTests` | Nonempty mixed types, namespace collisions, routing states, partial queries, two-page/refill behavior, cancellation. | IP04–IP06 |
| `AudiovisualSourceEvidenceTests` and Python contract fixtures | Wrong sequels/episodes rejected; true IDs accepted; missing evidence unverified; language not fabricated. | IP07 |
| `AudiovisualIdentityMigrationTests` | Old/new JSON, atomic failed write, idempotence, aliases, ambiguous entries retained, My List reconstruction. | IP08 |
| `AudiovisualPlaybackContextTests` | All forwarding/retry paths, source-independent resume, season separation, progress summary, details-tab closure. | IP08 |
| Extended bootstrap tests | Concurrent preparation, failed imports, pipe saturation, canceled waiter versus canceled preparation, same-session retry. | IP09 |
| `SourceResolutionSchedulingTests` | Shared caps, bounded queue, deduplication, first result before slow provider, terminal event, cleanup. | IP10 |
| `AutomaticResolutionRegressionTests` | At most one exhausted automatic crawl; fallback reuse; no fallback after cancel; Sub/Dub and episode forwarding through automatic native/browser choices. Index refresh/outage tests live in `tools/tests/test_anime_index_discovery.py`. | IP11 |
| `StreamingBodyLifetimeTests` | Stalled headers/body, segment inactivity, healthy long transfer, two sessions, closing one session. | IP12 |
| Extended Books/Dub tests | First useful results before stalled provider, partial success retention, timeout distinct from cancel, stale updates rejected. | IP13 |
| Extended image/grid tests | Coalescing, decode-size keys, unload/reload, subscriber ownership, bounded realization/resources. | IP14 |
| `PersistenceResponsivenessTests` | Dispatcher heartbeat under lock, ordered writes, delayed load ignored after session change, final flush. | IP15 |
| Extended settings/navigation/action tests | Persisted real behavior, health accuracy, metadata/source separation, actual keyboard/click outcomes. | IP16 |

New test seams must call the implementation being verified. Avoid tests that reproduce the matcher algorithm in the assertion, check only a string in XAML, or disable the provider and call an empty result proof of correct classification.

### 7.2 Live and desktop matrix

| Scenario | Required record |
|---|---|
| Fresh keyless Movies/TV/Cartoons | Chosen metadata provider, representative relevance/type results, query/page behavior, setup and provider state. |
| Configured/invalid credentials and provider outage | Explicit state/fallback outcome; current valid rows preserved; no infinite spinner or false zero-match success. |
| Film playback | Correct feature/version, source mode, first frame, advancing position, audio, seek, pause, close/reopen resume. |
| TV playback | Later-season episode, adjacent episode, season transition, correct content and per-unit resume; unavailable unit reported. |
| Language/source changes | Correct selected unit retained; actual track or provider evidence; unknown audio labeled; resume survives changed URL. |
| Native/browser fallback | Transport and replay headers correct; fallback page versus confirmed playback distinguished; close releases its session. |
| Anime preservation | Browse/details, Sub/Dub, automatic index selection and rotation, resume, next/previous, tab close, and MAL behavior where configured. No static provider selection is required. |
| Books/manga/standalone player | Supported reading/navigation/progress and file/URL player actions; useful provider failure behavior. |
| Downloads/My List/Watch Together | Queue restart and controls, identity-safe favorites reopening, two controlled clients and reconnect without duplicate handlers. |
| Layout/accessibility | 900/1280 DIP, supported densities/scales/themes, English/Arabic, keyboard, Windows DPI and secondary-monitor behavior. |
| Repeated lifecycle | 20 operation/tab cycles; resource/work counts before, during, and after; no impact on other active tabs. |

Controlled local media proves application integration independently of provider availability. Representative live movie and TV playback separately prove that the selected external path works at verification time. Both are required; record failures and unavailable samples rather than omitting them. A loaded web page, nonempty URL, or dispatched `PlayMediaMessage` is not proof of playback.

### 7.3 Measured targets

Before claiming a speed improvement, record baseline and repaired runs on the same machine/data conditions. Report first window, first useful catalog result, first verified source, first frame/audio, terminal result, cancellation settling, and peak/settled work counts separately. Use repeated warm/cold, success/failure/cancel samples; publish sample count and percentile method, not a p95 from an undisclosed handful of runs.

Initial **controlled-test acceptance targets**, to be calibrated once in IP01 and kept with the test environment record:

- A fast provider's completed results reach the view within 500 ms while another provider is deliberately stalled.
- Cancel/close detaches the UI operation immediately and terminates exclusively owned scraper/request work within 2 seconds on the controlled fixtures. Shared consumers remain active. If platform cleanup needs a different bound, document it with traces before changing the gate.
- Under three-second SQLite lock contention, a dispatcher heartbeat has no gap exceeding 100 ms on the reference test machine, while final progress persists correctly.
- Body deadline fixtures terminate within the configured deadline plus 500 ms scheduling tolerance. Healthy continuous streaming remains active.
- Counted automatic resolution starts at most one full exhausted crawl; shared process/image limits are never exceeded. Repeated lifecycle tests return active owned work counts to baseline.

These targets are proposed regression thresholds, not claims that the current app meets them or guarantees about live networks. Use the operation budgets in section 4 for predictable failure behavior. Never present the earlier code-derived 146-second resolver or 300-second pip budgets as stopwatch observations.

## 8. Build and test commands

Run from `C:\Users\user\animeapp` after code implementation. These commands are documented for future execution; they were not run to create this plan. Preserve `global.json` and locked dependency versions unless an actual compatibility issue requires a separate change.

```powershell
dotnet restore UniversalMediaOS.sln --locked-mode
dotnet build UniversalMediaOS.sln -c Debug --no-restore --nologo

$implementationDebugExe = Join-Path $PWD 'UniversalMediaOS.WPF/bin/Debug/net9.0-windows10.0.17763.0/UniversalMediaOS.WPF.exe'
$env:UNIVERSAL_MEDIA_OS_EXE = $implementationDebugExe
dotnet test UniversalMediaOS.Tests.E2E/UniversalMediaOS.Tests.E2E.csproj -c Debug --no-build --no-restore --logger 'trx;LogFileName=implementation-debug.trx'
```

During a work package, filter to the changed behavior and its relevant existing regressions; run the full suite at integration boundaries. Proposed suite names only become usable after their tests exist. The AppFixture must allocate a unique owned data root per run under IP01; do not reuse a user's profile as the test sandbox.

```powershell
# Example focused run after IP07/IP08 tests exist:
dotnet test UniversalMediaOS.Tests.E2E/UniversalMediaOS.Tests.E2E.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~AudiovisualSourceEvidenceTests|FullyQualifiedName~AudiovisualPlaybackContextTests|FullyQualifiedName~PlaybackResumeRegressionTests'

# Add/maintain Python protocol and matcher tests under tools/tests in IP07:
python -m unittest discover -s tools/tests -p 'test_*scraper*.py'
```

Release integration, after all required packages pass:

```powershell
dotnet build UniversalMediaOS.sln -c Release --no-restore --nologo
$implementationReleaseExe = Join-Path $PWD 'UniversalMediaOS.WPF/bin/Release/net9.0-windows10.0.17763.0/UniversalMediaOS.WPF.exe'
$env:UNIVERSAL_MEDIA_OS_EXE = $implementationReleaseExe
dotnet test UniversalMediaOS.Tests.E2E/UniversalMediaOS.Tests.E2E.csproj -c Release --no-build --no-restore --logger 'trx;LogFileName=implementation-release.trx'

$implementationPackageDir = Join-Path $PWD 'output/app-recovery-verified'
dotnet publish UniversalMediaOS.WPF/UniversalMediaOS.WPF.csproj -c Release --no-restore -o $implementationPackageDir
$env:UNIVERSAL_MEDIA_OS_EXE = Join-Path $implementationPackageDir 'UniversalMediaOS.WPF.exe'
# Run the implemented packaged-app smoke selection, then the live matrix.
git diff --check
```

Use a new empty package directory for each release candidate, retaining the prior candidate for comparison; do not mix stale binaries with a new publish. If build output is locked by a running app, use a consistent isolated artifacts/output path for that build and its tests, then set the exact executable override. Do not terminate a user's app merely to reuse a default output directory. Check and restore any temporary environment overrides when the verification session finishes.

The existing runner currently checks default bin output before running tests. IP01 must update that behavior before relying on it for alternate-output or packaged builds. Keep CI changes in the existing [release-validation workflow](C:/Users/user/animeapp/.github/workflows/release-validation.yml).

## 9. Integration and handoff process

Implement in these batches, allowing the independent branches described in section 2:

1. **Baseline and seams:** IP00–IP01. Establish path isolation and deterministic fixtures first.
2. **Visible UI repair:** IP02–IP03. Can run alongside the next batch once shared test helpers are available.
3. **Audiovisual foundation:** IP04–IP07. Freeze contracts, qualify metadata, then integrate paging and source evidence.
4. **Playback and bounded work:** IP08–IP12. IP09/IP11/IP12 may start earlier after their prerequisites. Integrate canonical resume and the scheduler together before the final multi-tab tests.
5. **Responsiveness and controls:** IP13–IP16. Preserve partial results, bound visual work, order persistence, and finish settings/action behavior.
6. **Acceptance and packaging:** IP17–IP18. Complete the live matrix and verify the actual deliverable.

For each package, record status as Planned, In progress, Implemented awaiting verification, Verified, or Blocked. A blocked provider qualification must state the exact unmet capability and allow independent packages to proceed. Avoid marking a package Verified on the basis of a passing compile or a previous task summary.

Each implementation handoff must include:

- Package and recovery requirement IDs covered.
- Changed production files and any new contracts/configuration values.
- Data compatibility/migration effects and recovery path.
- Targeted tests, relevant existing regressions, and their actual results.
- Screenshots/traces/live-flow evidence where the package requires them.
- Remaining limitations, with confirmed defects separated from unavailable external services.

Keep patch boundaries reviewable. Do not combine an unrelated provider migration, style rewrite, database migration, and playback lifecycle refactor in one commit. Remove obsolete compatibility code only after its consumers have migrated and tests prove the new path.

## 10. Active checklist and final acceptance

Track implementation, outstanding package acceptance and release acceptance in the separate [active recovery checklist](C:/Users/user/animeapp/docs/IMPLEMENTATION_CHECKLIST.md). Its checked items describe individual changes; they do not imply end-to-end acceptance of the application.

The next action is to verify the latest tested build through the user's core flows, reproduce failures and fix the first blocker with a focused change. Avoid further structural expansion without a demonstrated need. Update the separate checklist after each batch; keep this document for technical specifications and dated checkpoint history.

## 11. References

- [Recovery requirements and problem inventory](C:/Users/user/animeapp/docs/APP_RECOVERY_PLAN.md).
- [Current codebase audit](C:/Users/user/animeapp/docs/qa/codebase-audit-2026-09-09.md).
- [Git and earlier task evidence](C:/Users/user/animeapp/docs/qa/git-and-task-history-comparison-2026-09-09.md).
- [Rendered UI findings](C:/Users/user/animeapp/docs/qa/ui-audit-2026-09-09.md).
- [Movie/TV correctness findings](C:/Users/user/animeapp/docs/qa/movies-audit-2026-09-09.md).
- [Performance and lifetime findings](C:/Users/user/animeapp/docs/qa/performance-audit-2026-09-09.md).

The recovery document explains what is wrong and what better behavior means. This implementation plan is the ordered execution specification, including the additional verified audiovisual progress-wiring gap found while planning.
